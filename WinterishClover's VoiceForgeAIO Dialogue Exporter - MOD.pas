{
  Export ALL quest dialogue in a mod for VoiceForge AIO CSV Batch Generate.
  Apply this to the plugin's File Header (or literally any single record
  inside it) -- NOT to a specific quest. It auto-discovers every QUST record
  actually defined in that plugin and runs the exact same VoiceType
  resolution logic as the per-quest exporter across all of them, producing
  ONE combined CSV for the whole mod:
      EditorID,VoiceType,"Line text"

  This is the mod-wide sibling of
  "WinterishClover's VoiceForgeAIO Dialogue Exporter - QUST.pas" -- same
  resolution logic throughout (kept as a self-contained duplicate rather than
  a shared include, to match how the per-quest version is structured; if you
  fix something in one, port it to the other).

  Only covers dialogue owned by a QUST record, which is the overwhelming
  majority of voiced Skyrim content -- genuinely orphaned DIAL/INFO records
  with no owning quest are a rare edge case this doesn't attempt to catch.

  v1.0: initial mod-wide version, forked from the QUST exporter v4.1.
}
unit ModWideDialogueExport;

var
  slAllLines: TStringList;      // every row for the whole mod, in the tool's 3-column format
  slSpeakerCounts: TStringList; // VoiceType -> line count, for the summary message only
  slProcessedDials: TStringList;
  ExportDirectory, ModFileName: string;

//============================================================================
function Initialize: integer;
begin
  slAllLines := TStringList.Create;
  slSpeakerCounts := TStringList.Create;
  slSpeakerCounts.Sorted := True;
  slSpeakerCounts.Duplicates := dupIgnore;
  slProcessedDials := TStringList.Create;
  slProcessedDials.Sorted := True;
  slProcessedDials.Duplicates := dupIgnore;
end;

//============================================================================
function InfoFileName(QuestID, DialID: string; InfoFormID, RespNum: integer): string;
var
  qlen, dlen: integer;
begin
  qlen := Length(QuestID);
  dlen := Length(DialID);
  if qlen + dlen > 25 then begin
    if qlen > 10 then begin
      qlen := 10;
      dlen := 15;
    end else
      dlen := 10 - qlen + 15;
  end;
  Result := Format('%s_%s_%s_%d', [
    Copy(QuestID, 1, qlen),
    Copy(DialID, 1, dlen),
    IntToHex(InfoFormID and $FFFFFF, 8),
    RespNum
  ]);
end;

//============================================================================
// Standard CSV field escaping: double up embedded quotes, wrap in quotes.
function CsvQuote(const Value: string): string;
begin
  Result := '"' + StringReplace(Value, '"', '""', [rfReplaceAll]) + '"';
end;

//============================================================================
// Checks a record directly for its own voice-type field. NPC_ records store
// this in VTCK; TACT (Talking Activator) records -- e.g. a quest's "voice of
// Dagon" marker -- store it in VNAM instead. Returns '' if neither is found,
// meaning Rec is likely a placed reference rather than a base record.
function GetVoiceTypeFromRecord(Rec: IInterface): string;
var
  VoiceElem, VoiceType: IInterface;
begin
  Result := '';
  if not Assigned(Rec) then Exit;

  VoiceElem := ElementByName(Rec, 'VTCK - Voice');
  if not Assigned(VoiceElem) then VoiceElem := ElementByName(Rec, 'VTCK');
  if not Assigned(VoiceElem) then VoiceElem := ElementByName(Rec, 'VNAM - Voice Type');
  if not Assigned(VoiceElem) then VoiceElem := ElementByName(Rec, 'VNAM');

  if Assigned(VoiceElem) then begin
    VoiceType := LinksTo(VoiceElem);
    if Assigned(VoiceType) then begin
      Result := EditorID(VoiceType);
      if Result = '' then Result := Name(VoiceType);
    end;
  end;
end;

//============================================================================
// Resolves an actor-like record to the EditorID of its ACTUAL assigned
// VoiceType, NOT its own EditorID. Handles two shapes:
//   1. Rec is already a base record with its own voice field (NPC_'s VTCK,
//      or TACT's VNAM) -- checked directly first.
//   2. Rec is a placed reference (ACHR, REFR, ACRE, PGRE, etc. -- these all
//      use the same "NAME" subrecord for their base object) pointing AT one
//      of the above -- resolved via NAME and checked again.
// Falls back to raw identity only if neither resolves, with a clear warning
// printed so that line can be spot-checked by hand.
function ResolveVoiceTypeForActor(ActorRec: IInterface): string;
var
  BaseRec: IInterface;
begin
  Result := '';
  if not Assigned(ActorRec) then Exit;

  Result := GetVoiceTypeFromRecord(ActorRec);
  if Result <> '' then Exit;

  BaseRec := LinksTo(ElementByPath(ActorRec, 'NAME'));
  if Assigned(BaseRec) then begin
    Result := GetVoiceTypeFromRecord(BaseRec);
    if Result <> '' then Exit;
  end else
    BaseRec := ActorRec;

  Result := EditorID(BaseRec);
  if Result = '' then Result := Name(BaseRec);
  if Result <> '' then
    AddMessage('  [!] No VoiceType (VTCK/VNAM) found for "' + Result + '" -- used its EditorID as a fallback. Double-check this one.');
end;

//============================================================================
// Resolves a quest alias ID to a VoiceType EditorID. Some aliases (e.g. a
// deity's dialogue "voice marker") don't resolve to any physical actor at
// all -- they carry a direct Voice Type filter (VTYP) on the alias itself,
// checked FIRST since it's the most explicit, unambiguous source. Otherwise,
// resolves whatever actor the alias points at and uses THAT actor's real
// VoiceType (never the alias's or actor's own name).
function ResolveVoiceTypeForAlias(Quest: IInterface; AliasID: integer): string;
var
  AliasesEl, Alias, VoiceElem, VoiceType, ActorRec: IInterface;
  i, ThisID: integer;
  AliasName: string;
begin
  Result := '';
  AliasesEl := ElementByName(Quest, 'Aliases');
  if not Assigned(AliasesEl) then Exit;

  for i := 0 to Pred(ElementCount(AliasesEl)) do begin
    Alias := ElementByIndex(AliasesEl, i);
    ThisID := GetElementNativeValues(Alias, 'ALST - Reference Alias ID');
    if ThisID = 0 then
      try ThisID := StrToInt(GetElementEditValues(Alias, 'ALST - Reference Alias ID')); except end;
    if ThisID <> AliasID then Continue;

    // 1. Direct Voice Type filter on the alias itself.
    VoiceElem := ElementByPath(Alias, 'VTYP - Voice Type');
    if not Assigned(VoiceElem) then VoiceElem := ElementByPath(Alias, 'VTYP');
    if Assigned(VoiceElem) then begin
      VoiceType := LinksTo(VoiceElem);
      if Assigned(VoiceType) then begin
        Result := EditorID(VoiceType);
        if Result = '' then Result := Name(VoiceType);
        if Result <> '' then Exit;
      end;
    end;

    // 2. Otherwise, resolve whichever actor this alias points at and use
    //    THAT actor's real voice field.
    ActorRec := LinksTo(ElementByPath(Alias, 'ALUA - Unique Actor'));
    if not Assigned(ActorRec) then ActorRec := LinksTo(ElementByPath(Alias, 'ALUA'));
    if not Assigned(ActorRec) then ActorRec := LinksTo(ElementByPath(Alias, 'ALFR - Forced Reference'));
    if not Assigned(ActorRec) then ActorRec := LinksTo(ElementByPath(Alias, 'ALFR'));
    if Assigned(ActorRec) then begin
      Result := ResolveVoiceTypeForActor(ActorRec);
      if Result <> '' then Exit;
    end;

    // 3. Last resort: name the alias itself, flagged clearly for a manual check.
    AliasName := GetElementEditValues(Alias, 'ALID - Alias Name');
    if AliasName = '' then AliasName := GetElementEditValues(Alias, 'ALID');
    if AliasName = '' then AliasName := 'Alias' + IntToStr(AliasID);
    Result := AliasName;
    AddMessage('  [!] Could not resolve a VoiceType for alias "' + AliasName + '" -- using the alias name as a fallback. Double-check this one.');
    Exit;
  end;
end;

//============================================================================
// Scans a Conditions list for whichever speaker-identifying function is
// present, in strict priority order -- GetIsVoiceType beats GetIsAliasRef
// beats GetIsID -- run as full separate passes rather than "whichever comes
// first," so incidental condition ordering can never flip the result.
function ScanConditionsForVoiceType(Quest: IInterface; Conditions: IInterface): string;
var
  i, AliasID: integer;
  Condition, Elem: IInterface;
  Func: string;
begin
  Result := '';
  if not Assigned(Conditions) then Exit;

  for i := 0 to Pred(ElementCount(Conditions)) do begin
    Condition := ElementByIndex(Conditions, i);
    Func := GetElementEditValues(Condition, 'CTDA\Function');
    if Func = 'GetIsVoiceType' then begin
      Elem := LinksTo(ElementByPath(Condition, 'CTDA\Voice Type'));
      if not Assigned(Elem) then Elem := LinksTo(ElementByPath(Condition, 'CTDA\Parameter #1'));
      if Assigned(Elem) then begin
        Result := EditorID(Elem);
        if Result = '' then Result := Name(Elem);
        if Result <> '' then Exit;
      end;
    end;
  end;

  for i := 0 to Pred(ElementCount(Conditions)) do begin
    Condition := ElementByIndex(Conditions, i);
    Func := GetElementEditValues(Condition, 'CTDA\Function');
    if Func = 'GetIsAliasRef' then begin
      AliasID := GetElementNativeValues(Condition, 'CTDA\Parameter #1');
      if AliasID = 0 then
        try AliasID := StrToInt(GetElementEditValues(Condition, 'CTDA\Parameter #1')); except end;
      Result := ResolveVoiceTypeForAlias(Quest, AliasID);
      if Result <> '' then Exit;
    end;
  end;

  for i := 0 to Pred(ElementCount(Conditions)) do begin
    Condition := ElementByIndex(Conditions, i);
    Func := GetElementEditValues(Condition, 'CTDA\Function');
    if Func = 'GetIsID' then begin
      Elem := LinksTo(ElementByPath(Condition, 'CTDA\Base Object'));
      if not Assigned(Elem) then Elem := LinksTo(ElementByPath(Condition, 'CTDA\Parameter #1'));
      if not Assigned(Elem) then Elem := LinksTo(ElementByPath(Condition, 'CTDA\Parameter #2'));
      if Assigned(Elem) then begin
        Result := ResolveVoiceTypeForActor(Elem);
        if Result <> '' then Exit;
      end;
    end;
  end;
end;

//============================================================================
function GetVoiceTypeForInfo(Quest: IInterface; Info: IInterface; QuestConditions: IInterface): string;
var
  Speaker: IInterface;
  Conditions: IInterface;
begin
  Result := '';

  Speaker := ElementByName(Info, 'ANAM - Speaker');
  if Assigned(Speaker) then begin
    Speaker := LinksTo(Speaker);
    if Assigned(Speaker) then begin
      Result := ResolveVoiceTypeForActor(Speaker);
      if Result <> '' then Exit;
    end;
  end;

  Conditions := ElementByName(Info, 'Conditions');
  Result := ScanConditionsForVoiceType(Quest, Conditions);
  if Result <> '' then Exit;

  if Assigned(QuestConditions) then begin
    Result := ScanConditionsForVoiceType(Quest, QuestConditions);
    if Result <> '' then Exit;
  end;

  Result := 'Unknown';
  AddMessage('  [!] Could not determine a VoiceType for this line at all -- check it manually.');
end;

//============================================================================
function SanitizeVoiceType(const RawName: string): string;
var
  Result0: string;
begin
  Result0 := RawName;
  Result0 := StringReplace(Result0, ' ', '_', [rfReplaceAll]);
  Result0 := StringReplace(Result0, ':', '', [rfReplaceAll]);
  Result0 := StringReplace(Result0, '\', '', [rfReplaceAll]);
  Result0 := StringReplace(Result0, '/', '', [rfReplaceAll]);
  Result0 := StringReplace(Result0, '?', '', [rfReplaceAll]);
  Result0 := StringReplace(Result0, '*', '', [rfReplaceAll]);
  Result0 := StringReplace(Result0, '<', '', [rfReplaceAll]);
  Result0 := StringReplace(Result0, '>', '', [rfReplaceAll]);
  Result0 := StringReplace(Result0, '|', '', [rfReplaceAll]);
  Result0 := StringReplace(Result0, ',', '', [rfReplaceAll]);
  Result0 := StringReplace(Result0, '"', '', [rfReplaceAll]);
  Result := Result0;
end;

//============================================================================
// Adds one row in VoiceForge AIO's format: EditorID,VoiceType,"Text"
procedure AddLine(SpeakerName, VoiceFileName, ResponseText: string);
var
  VoiceType: string;
  Index: integer;
  Count: integer;
begin
  if SpeakerName = '' then SpeakerName := 'Unknown';
  VoiceType := SanitizeVoiceType(SpeakerName);

  slAllLines.Add(VoiceFileName + ',' + VoiceType + ',' + CsvQuote(ResponseText));

  Index := slSpeakerCounts.IndexOf(SpeakerName);
  if Index = -1 then
    slSpeakerCounts.AddObject(SpeakerName, TObject(1))
  else begin
    Count := Integer(slSpeakerCounts.Objects[Index]) + 1;
    slSpeakerCounts.Objects[Index] := TObject(Count);
  end;
end;

//============================================================================
procedure ProcessDialogueWithSpeaker(Quest: IInterface; Dialogue: IInterface; ForcedSpeaker: string);
var
  i, r: integer;
  InfoGroup, Info: IInterface;
  Responses, Response: IInterface;
  ResponseNumber: integer;
  VoiceFileName, ResponseText, SpeakerName: string;
  QuestConditions: IInterface;
  DialHex: string;
begin
  InfoGroup := ChildGroup(Dialogue);
  if not Assigned(InfoGroup) then Exit;

  // Tracked across the WHOLE mod run (not reset per-quest), so a DIAL
  // somehow referenced by more than one quest is still only exported once.
  DialHex := IntToHex(GetLoadOrderFormID(Dialogue), 8);
  if slProcessedDials.IndexOf(DialHex) >= 0 then Exit;
  slProcessedDials.Add(DialHex);

  QuestConditions := ElementByPath(Quest, 'Quest Dialogue Conditions\Conditions');

  for i := 0 to Pred(ElementCount(InfoGroup)) do begin
    Info := ElementByIndex(InfoGroup, i);
    if GetIsDeleted(Info) then Continue;
    Responses := ElementByName(Info, 'Responses');
    if not Assigned(Responses) then Continue;

    if ForcedSpeaker <> '' then
      SpeakerName := ForcedSpeaker
    else
      SpeakerName := GetVoiceTypeForInfo(Quest, Info, QuestConditions);

    for r := 0 to Pred(ElementCount(Responses)) do begin
      Response := ElementByIndex(Responses, r);
      ResponseNumber := GetElementNativeValues(Response, 'TRDT\Response number');
      ResponseText := GetElementEditValues(Response, 'NAM1 - Response Text');
      if Trim(ResponseText) = '' then Continue;
      VoiceFileName := InfoFileName(EditorID(Quest), EditorID(Dialogue), GetLoadOrderFormID(Info), ResponseNumber);
      AddLine(SpeakerName, VoiceFileName, ResponseText);
    end;
  end;
end;

//============================================================================
procedure ProcessDialogue(Quest: IInterface; Dialogue: IInterface);
begin
  ProcessDialogueWithSpeaker(Quest, Dialogue, '');
end;

//============================================================================
procedure ProcessDialogueBranch(Quest: IInterface; DialogueBranch: IInterface);
var
  i: integer;
  Dialogue: IInterface;
begin
  for i := 0 to Pred(ReferencedByCount(DialogueBranch)) do begin
    Dialogue := ReferencedByIndex(DialogueBranch, i);
    if Signature(Dialogue) <> 'DIAL' then Continue;
    if GetIsDeleted(Dialogue) then Continue;
    ProcessDialogue(Quest, Dialogue);
  end;
end;

//============================================================================
procedure ProcessScene(Quest: IInterface; Scene: IInterface);
var
  Actions, Action, Dial: IInterface;
  i: integer;
  ActorAliasID: integer;
  SpeakerName, ActionType: string;
begin
  Actions := ElementByPath(Scene, 'Actions');
  if not Assigned(Actions) then Exit;

  for i := 0 to Pred(ElementCount(Actions)) do begin
    Action := ElementByIndex(Actions, i);
    ActionType := GetElementEditValues(Action, 'ANAM - Type');
    if not SameText(ActionType, 'Dialogue') then Continue;

    Dial := LinksTo(ElementByPath(Action, 'Dialogue\DATA - Topic'));
    if not Assigned(Dial) then Continue;
    if GetIsDeleted(Dial) then Continue;

    ActorAliasID := GetElementNativeValues(Action, 'ALID - Actor ID');
    if ActorAliasID = 0 then
      try ActorAliasID := StrToInt(GetElementEditValues(Action, 'ALID - Actor ID')); except end;
    if ActorAliasID = 0 then
      try ActorAliasID := StrToInt(GetElementEditValues(Action, 'ALID')); except end;
    SpeakerName := ResolveVoiceTypeForAlias(Quest, ActorAliasID);

    ProcessDialogueWithSpeaker(Quest, Dial, SpeakerName);
  end;
end;

//============================================================================
procedure ExportQuest(Quest: IInterface);
var
  i: integer;
  ReferencedRecord: IInterface;
begin
  // Pass 1: SCEN records first, so scene DIALs get proper speaker names
  // before the general DIAL pass can claim them as Unknown.
  for i := 0 to Pred(ReferencedByCount(Quest)) do begin
    ReferencedRecord := ReferencedByIndex(Quest, i);
    if Signature(ReferencedRecord) = 'SCEN' then
      ProcessScene(Quest, ReferencedRecord);
  end;

  // Pass 2: DLBR chains and any remaining DIALs not already covered by scenes.
  for i := 0 to Pred(ReferencedByCount(Quest)) do begin
    ReferencedRecord := ReferencedByIndex(Quest, i);
    if Signature(ReferencedRecord) = 'DLBR' then
      ProcessDialogueBranch(Quest, ReferencedRecord)
    else if Signature(ReferencedRecord) = 'DIAL' then
      ProcessDialogue(Quest, ReferencedRecord);
  end;
end;

//============================================================================
// Entry point: apply to the plugin's File Header, or any single record
// inside it -- NOT to a specific quest. Auto-discovers every QUST record
// actually defined in that plugin (not inherited from masters) and runs the
// same per-quest export logic across all of them into one combined list.
function Process(e: IInterface): integer;
var
  aFile: IInterface;
  QuestGroup, Quest: IInterface;
  i, ProcessedCount: integer;
begin
  aFile := GetFile(e);
  if not Assigned(aFile) then begin
    AddMessage('Could not determine the plugin file for the selected record.');
    Exit;
  end;

  ModFileName := GetFileName(aFile);
  AddMessage('=== EXPORTING ALL DIALOGUE FOR MOD: ' + ModFileName);

  if ContainerStates(aFile) and (1 shl csRefsBuild) = 0 then begin
    AddMessage('References are not built for this file.');
    AddMessage('Use Right click \ Other \ Build Reference Info menu and try again.');
    Exit;
  end;

  QuestGroup := GroupBySignature(aFile, 'QUST');
  if not Assigned(QuestGroup) then begin
    AddMessage('No QUST (quest) records found in this file.');
    Exit;
  end;

  ProcessedCount := 0;
  for i := 0 to Pred(ElementCount(QuestGroup)) do begin
    Quest := ElementByIndex(QuestGroup, i);
    if GetIsDeleted(Quest) then Continue;
    AddMessage('  Quest: ' + Name(Quest));
    ExportQuest(Quest);
    Inc(ProcessedCount);
  end;

  AddMessage('Processed ' + IntToStr(ProcessedCount) + ' quest(s).');
end;

//============================================================================
function Finalize: integer;
var
  i: integer;
  FileName: string;
  dlgSave: TSaveDialog;
begin
  if slAllLines.Count = 0 then begin
    AddMessage('No dialogue found in this mod.');
    slAllLines.Free;
    slSpeakerCounts.Free;
    slProcessedDials.Free;
    Exit;
  end;

  dlgSave := TSaveDialog.Create(nil);
  try
    dlgSave.Title := 'Select output folder for the CSV file';
    dlgSave.Filter := 'CSV files (*.csv)|*.csv';
    dlgSave.FileName := 'output_folder.csv';
    dlgSave.Options := [ofPathMustExist];
    if not dlgSave.Execute then begin
      AddMessage('Export cancelled.');
      Exit;
    end;
    ExportDirectory := ExtractFilePath(dlgSave.FileName);
    if (Length(ExportDirectory) > 0) and (ExportDirectory[Length(ExportDirectory)] = '\') then
      ExportDirectory := Copy(ExportDirectory, 1, Length(ExportDirectory) - 1);
  finally
    dlgSave.Free;
  end;

  FileName := ExportDirectory + '\' + ChangeFileExt(ModFileName, '') + '.csv';
  try
    slAllLines.SaveToFile(FileName);
    AddMessage('Created: ' + ExtractFileName(FileName) + ' (' + IntToStr(slAllLines.Count) + ' lines)');
  except
    on E: Exception do
      AddMessage('Error saving ' + FileName + ': ' + E.Message);
  end;

  AddMessage('');
  AddMessage('VoiceTypes found across this mod:');
  for i := 0 to Pred(slSpeakerCounts.Count) do
    AddMessage('  ' + slSpeakerCounts[i] + ': ' + IntToStr(Integer(slSpeakerCounts.Objects[i])) + ' line(s)');
  AddMessage('');
  AddMessage('Export complete. Ready to load directly into VoiceForge AIO''s CSV Batch Generate.');
  AddMessage('Reminder: make sure every VoiceType listed above has a row in the Voice Mapping table before running the batch.');

  slAllLines.Free;
  slSpeakerCounts.Free;
  slProcessedDials.Free;
end;

end.
