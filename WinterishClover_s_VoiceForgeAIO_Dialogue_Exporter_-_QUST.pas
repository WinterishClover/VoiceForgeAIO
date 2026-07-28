{
  Export quest dialogues for VoiceForge AIO CSV Batch Generate.
  Apply to quest record(s) - creates ONE combined CSV per quest:
      EditorID,VoiceType,"Line text"
  ready to drop straight into VoiceForge AIO's CSV Batch Generate, no manual
  merging needed.

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
procedure InfoVoiceTypes(Info: IInterface; lstVoice: TStringList);
var
  Conditions: IInterface;
  i: integer;
  Condition: IInterface;
  ConditionFunction: string;
  Elem: IInterface;
  SpeakerName: string;
begin
  lstVoice.Clear;
  Conditions := ElementByName(Info, 'Conditions');
  if Assigned(Conditions) then begin
    for i := 0 to Pred(ElementCount(Conditions)) do begin
      Condition := ElementByIndex(Conditions, i);
      ConditionFunction := GetElementEditValues(Condition, 'CTDA\Function');
      if ConditionFunction = 'GetIsID' then begin
        Elem := LinksTo(ElementByPath(Condition, 'CTDA\Base Object'));
        if not Assigned(Elem) then Elem := LinksTo(ElementByPath(Condition, 'CTDA\Parameter #1'));
        if not Assigned(Elem) then Elem := LinksTo(ElementByPath(Condition, 'CTDA\Parameter #2'));
        if Assigned(Elem) then begin
          SpeakerName := EditorID(Elem);
          if SpeakerName = '' then SpeakerName := Name(Elem);
          lstVoice.Add(SpeakerName);
        end;
      end else if ConditionFunction = 'GetIsVoiceType' then begin
        Elem := LinksTo(ElementByPath(Condition, 'CTDA\Voice Type'));
        if Assigned(Elem) then begin
          SpeakerName := EditorID(Elem);
          if SpeakerName = '' then SpeakerName := Name(Elem);
          lstVoice.Add(SpeakerName);
        end;
      end;
    end;
  end;
end;

//============================================================================
function GetSpeakerName(Info: IInterface; QuestConditions: IInterface): string;
var
  lstVoice: TStringList;
  Speaker: IInterface;
  Conditions, Condition, Elem: IInterface;
  i: integer;
begin
  Result := 'Unknown';
  lstVoice := TStringList.Create;
  try
    Speaker := ElementByName(Info, 'ANAM - Speaker');
    if Assigned(Speaker) then begin
      Speaker := LinksTo(Speaker);
      if Assigned(Speaker) then begin
        Result := EditorID(Speaker);
        if Result = '' then Result := Name(Speaker);
        Exit;
      end;
    end;
    InfoVoiceTypes(Info, lstVoice);
    if lstVoice.Count > 0 then begin
      Result := lstVoice[0];
      Exit;
    end;
    if Assigned(QuestConditions) then begin
      for i := 0 to Pred(ElementCount(QuestConditions)) do begin
        Condition := ElementByIndex(QuestConditions, i);
        if GetElementEditValues(Condition, 'CTDA\Function') = 'GetIsID' then begin
          Elem := LinksTo(ElementByPath(Condition, 'CTDA\Base Object'));
          if Assigned(Elem) then begin
            Result := EditorID(Elem);
            if Result = '' then Result := Name(Elem);
            Exit;
          end;
        end;
      end;
    end;
  finally
    lstVoice.Free;
  end;
end;

//============================================================================
// Resolve actor alias ID -> speaker name using quest alias list
function GetAliasActorName(Quest: IInterface; AliasID: integer): string;
var
  AliasesEl, Alias, ActorRec: IInterface;
  i: integer;
  ThisID: integer;
begin
  Result := 'Alias' + IntToStr(AliasID);
  AliasesEl := ElementByName(Quest, 'Aliases');
  if not Assigned(AliasesEl) then Exit;
  for i := 0 to Pred(ElementCount(AliasesEl)) do begin
    Alias := ElementByIndex(AliasesEl, i);
ThisID := GetElementNativeValues(Alias, 'ALST - Reference Alias ID');
    if ThisID = 0 then
      try ThisID := StrToInt(GetElementEditValues(Alias, 'ALST - Reference Alias ID')); except end;
    if ThisID <> AliasID then Continue;
    // Try Unique Actor (ALUA)
    ActorRec := LinksTo(ElementByPath(Alias, 'ALUA - Unique Actor'));
    if not Assigned(ActorRec) then ActorRec := LinksTo(ElementByPath(Alias, 'ALUA'));
    if Assigned(ActorRec) then begin
      Result := EditorID(ActorRec);
      if Result = '' then Result := Name(ActorRec);
    end else begin
      // Fall back to alias name
      Result := GetElementEditValues(Alias, 'ALID - Alias Name');
      if Result = '' then Result := GetElementEditValues(Alias, 'ALID');
      if Result = '' then Result := 'Alias' + IntToStr(AliasID);
    end;
    Exit;
  end;
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
      SpeakerName := GetSpeakerName(Info, QuestConditions);

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
    SpeakerName := GetAliasActorName(Quest, ActorAliasID);

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
