{
  Export quest dialogues for VoiceForge AIO CSV Batch Generate.
  Apply to quest record(s) - creates ONE combined CSV per quest:
      EditorID,VoiceType,"Line text"
  ready to drop straight into VoiceForge AIO's CSV Batch Generate, no manual
  merging needed.

  v4.1: Added VNAM support for TACT (Talking Activator) records -- these
  store their voice differently than NPC_ (VNAM vs VTCK). Also generalized
  placed-reference resolution: previously only ACHR was resolved down to its
  base object via NAME; now any placed reference (REFR included) is, which
  matters because a quest alias's "Forced Reference" commonly points at a
  generic REFR placed instance of a talking activator, not the TACT base
  record itself.

  v4.0: Fixed VoiceType resolution to always resolve through the actor's
  ACTUAL assigned VoiceType (VTCK), never an actor's own EditorID -- the
  previous version mixed "which actor is speaking" conditions (GetIsID) with
  "what VoiceType" conditions (GetIsVoiceType) as if interchangeable, which
  could give the same character two different values depending on which
  condition happened to appear first on a given dialogue line. Also added
  GetIsAliasRef handling (previously unhandled -> fell through to "Unknown"),
  including alias-level Voice Type filters (VTYP) for voice-marker-style
  aliases with no physical actor (e.g. a deity's dialogue marker). Any line
  where resolution still has to fall back to a raw identity gets a "[!]"
  warning printed to the message log for manual double-checking.

  v3.0: Combined output into a single CSV per quest with a VoiceType column
  (previously split into one file per speaker, with VoiceType only implied by
  filename). Also fixes response text with embedded quote characters not
  being escaped, which could produce malformed CSV.
}
unit SpeakerSeparatedDialogueExport;

var
  slAllLines: TStringList;      // every row for this quest, in the tool's 3-column format
  slSpeakerCounts: TStringList; // SpeakerName -> line count, for the summary message only
  slProcessedDials: TStringList;
  ExportDirectory, QuestEditorID: string;

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
// The original script never escaped embedded quote characters in response
// text, which would silently produce malformed CSV whenever a line of
// dialogue contained a " character.
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
//      of the above -- resolved via NAME and checked again. This is the
//      common shape for a quest alias's "Forced Reference" pointing at a
//      placed talking-activator marker rather than the TACT base itself.
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
// which is checked FIRST since it's the most explicit, unambiguous source.
// Otherwise, resolves whatever actor the alias points at and uses THAT
// actor's real VTCK (never the alias's or actor's own name).
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

    // 1. Direct Voice Type filter on the alias itself (handles voice-marker-
    //    style aliases with no physical actor, like the Dagon example).
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

    // 2. Otherwise, resolve whichever actor this alias points at (Unique
    //    Actor, or Forced Reference) and use THAT actor's real VTCK.
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
// present, in strict priority order -- GetIsVoiceType (unambiguous) beats
// GetIsAliasRef (resolves through the alias) beats GetIsID (an actor's own
// identity, needs one more lookup to find its real VoiceType). This runs as
// two full passes rather than "whichever comes first in the list," since the
// original bug was exactly that: an incidental condition ordering difference
// between two INFOs for the same character produced two different results.
function ScanConditionsForVoiceType(Quest: IInterface; Conditions: IInterface): string;
var
  i, AliasID: integer;
  Condition, Elem: IInterface;
  Func: string;
begin
  Result := '';
  if not Assigned(Conditions) then Exit;

  // Pass 1: explicit GetIsVoiceType wins outright, regardless of position.
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

  // Pass 2: GetIsAliasRef -- resolve through the alias (handles VTYP-only markers).
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

  // Pass 3: GetIsID -- an actor identity, not a VoiceType. Resolve that
  // actor's real VTCK rather than using its EditorID directly.
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

  // ANAM direct speaker override on the INFO -- still needs the extra hop
  // to that actor's real VTCK, never its own EditorID.
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
// Sanitize a speaker name for use as the VoiceType column -- strip characters
// that would be awkward as a Skyrim voice-folder name (mirrors the character
// stripping the original script applied to per-speaker filenames).
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
  Result0 := StringReplace(Result0, ',', '', [rfReplaceAll]); // keep it CSV-safe unquoted
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

  // Track a per-speaker count purely for the completion summary message.
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
  Actions, Action, DataTopic, Dial: IInterface;
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
  QuestEditorID := EditorID(Quest);
  slProcessedDials.Clear;

  // Pass 1: process SCEN records first so scene DIALs get proper speaker names
  // before the general DIAL pass can claim them as Unknown
  for i := 0 to Pred(ReferencedByCount(Quest)) do begin
    ReferencedRecord := ReferencedByIndex(Quest, i);
    if Signature(ReferencedRecord) = 'SCEN' then
      ProcessScene(Quest, ReferencedRecord);
  end;

  // Pass 2: process DLBR chains and any remaining DIALs not already covered by scenes
  for i := 0 to Pred(ReferencedByCount(Quest)) do begin
    ReferencedRecord := ReferencedByIndex(Quest, i);
    if Signature(ReferencedRecord) = 'DLBR' then
      ProcessDialogueBranch(Quest, ReferencedRecord)
    else if Signature(ReferencedRecord) = 'DIAL' then
      ProcessDialogue(Quest, ReferencedRecord);
  end;
end;

//============================================================================
function Process(e: IInterface): integer;
begin
  if Signature(e) <> 'QUST' then begin
    AddMessage('This script must be applied to a QUST (Quest) record.');
    Exit;
  end;
  AddMessage('=== EXPORTING DIALOGUE FOR VOICEFORGE AIO: ' + Name(e));
  e := MasterOrSelf(e);
  if ContainerStates(GetFile(e)) and (1 shl csRefsBuild) = 0 then begin
    AddMessage('Skipping quest, references are not built for file ' + GetFileName(e));
    AddMessage('Use Right click \ Other \ Build Reference Info menu and try again.');
    Exit;
  end;
  ExportQuest(e);
end;

//============================================================================
function Finalize: integer;
var
  i: integer;
  FileName: string;
  dlgSave: TSaveDialog;
begin
  if slAllLines.Count = 0 then begin
    AddMessage('No dialogue found for this quest.');
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

  FileName := ExportDirectory + '\' + QuestEditorID + '.csv';
  try
    slAllLines.SaveToFile(FileName);
    AddMessage('Created: ' + ExtractFileName(FileName) + ' (' + IntToStr(slAllLines.Count) + ' lines)');
  except
    on E: Exception do
      AddMessage('Error saving ' + FileName + ': ' + E.Message);
  end;

  AddMessage('');
  AddMessage('VoiceTypes found in this quest:');
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
