using Mutagen.Bethesda;
using Mutagen.Bethesda.Installs;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace VoiceForgeAIO.PluginDialogue;

/// <summary>A plugin file found in the Skyrim Data folder (or an extra folder such as Mod Organizer's mods folder).</summary>
internal sealed record PluginFile(string Name, string Path, string Source, long Size);

/// <summary>One voiced response of one dialogue line (INFO): the file name the game expects, who speaks it, and the words.</summary>
internal sealed record DialogueLine(string Id, string Quest, string Topic, string InfoFormId, int Response, string VoiceType, string Speakers, string Text);

internal sealed class PluginDialogue
{
    public string PluginName { get; init; } = "";
    public string PluginPath { get; init; } = "";
    public List<DialogueLine> Lines { get; } = new();
    public List<string> Warnings { get; } = new();
}

/// <summary>
/// Reads a plugin's dialogue straight from the plugin, so no xEdit script and no CSV are needed.
/// It produces the same rows the xEdit exporters write -- file name (Quest_Topic_FormID_Response),
/// VoiceType, text -- and resolves the VoiceType the same way: the INFO's own Speaker, then its
/// GetIsVoiceType / GetIsAliasRef / GetIsID conditions, then the quest's dialogue conditions;
/// topics driven by a scene take the voice of the scene action's actor alias.
/// </summary>
internal static class PluginDialogueService
{
    private static readonly string[] PluginExtensions = { ".esp", ".esm", ".esl" };

    /// <summary>Files from the base game and Creation Club, hidden from the plugin list by default (they hold no dialogue to voice).</summary>
    private static readonly System.Text.RegularExpressions.Regex CreationClub = new(@"^cc[a-z0-9]+\d{3}-", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    public static bool IsOfficialFile(string name) =>
        new[] { "Skyrim.esm", "Update.esm", "Dawnguard.esm", "HearthFires.esm", "Dragonborn.esm" }.Contains(name, StringComparer.OrdinalIgnoreCase)
        || CreationClub.IsMatch(name);

    // ---------------------------------------------------------------- finding plugins

    /// <summary>The Skyrim Special Edition Data folder (Steam, GOG or the Bethesda registry entry), or null if it can't be found.</summary>
    public static string? DetectDataFolder()
    {
        try { return GameLocations.TryGetDataFolder(GameRelease.SkyrimSE, out var folder) ? folder.Path : null; }
        catch { return null; }
    }

    /// <summary>
    /// Every plugin in the Data folder, plus -- for Mod Organizer style setups -- in the extra folder and the folders directly inside it
    /// (mods\ModName\Plugin.esp). When a name appears twice the Data folder's copy is the one used.
    /// </summary>
    public static List<PluginFile> ScanPlugins(string? dataFolder, string? extraFolder)
    {
        var found = new Dictionary<string, PluginFile>(StringComparer.OrdinalIgnoreCase);

        void AddFrom(string folder, string source)
        {
            if (!Directory.Exists(folder)) return;
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(folder); } catch { return; }
            foreach (var file in files)
            {
                if (!PluginExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase)) continue;
                var name = Path.GetFileName(file);
                if (found.ContainsKey(name)) continue;
                long size = 0;
                try { size = new FileInfo(file).Length; } catch { }
                found[name] = new PluginFile(name, file, source, size);
            }
        }

        if (!string.IsNullOrWhiteSpace(dataFolder)) AddFrom(dataFolder.Trim(), "Data folder");
        if (!string.IsNullOrWhiteSpace(extraFolder) && Directory.Exists(extraFolder.Trim()))
        {
            var extra = extraFolder.Trim();
            AddFrom(extra, Path.GetFileName(extra.TrimEnd('\\', '/')));
            IEnumerable<string> subfolders;
            try { subfolders = Directory.EnumerateDirectories(extra); } catch { subfolders = Array.Empty<string>(); }
            foreach (var sub in subfolders) AddFrom(sub, Path.GetFileName(sub));
        }
        return found.Values.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    // ---------------------------------------------------------------- the file name the game looks for

    /// <summary>
    /// "Quest_Topic_FormID_Response": the Creation Kit shortens the quest and topic EditorIDs to fit 25 characters between them
    /// (quest at most 10 when they don't fit). Same rule as the xEdit exporters' InfoFileName.
    /// </summary>
    public static string InfoFileName(string questId, string topicId, uint infoObjectId, int response)
    {
        int qlen = questId.Length, dlen = topicId.Length;
        if (qlen + dlen > 25)
        {
            if (qlen > 10) { qlen = 10; dlen = 15; }
            else dlen = 10 - qlen + 15;
        }
        return $"{questId[..Math.Min(qlen, questId.Length)]}_{topicId[..Math.Min(dlen, topicId.Length)]}_{infoObjectId & 0xFFFFFF:X8}_{response}";
    }

    /// <summary>The folder name of a VoiceType under Sound\Voice\Plugin.esp\ (illegal characters removed, spaces to underscores).</summary>
    public static string SanitizeVoiceType(string raw)
    {
        var s = (raw ?? "").Replace(' ', '_');
        foreach (var c in new[] { ':', '\\', '/', '?', '*', '<', '>', '|', ',', '"' }) s = s.Replace(c.ToString(), "");
        return s;
    }

    // ---------------------------------------------------------------- reading

    /// <summary>Loads the plugin and, first, every master it needs, so speakers and VoiceTypes defined elsewhere (Skyrim.esm NPCs) resolve.</summary>
    private static List<ISkyrimModGetter> LoadWithMasters(string pluginPath, Dictionary<string, string> index, List<string> warnings, out ISkyrimModGetter target)
    {
        var loaded = new List<ISkyrimModGetter>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        ISkyrimModGetter Open(string path) => SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE);

        void Visit(ISkyrimModGetter mod)
        {
            foreach (var master in mod.MasterReferences)
            {
                var name = master.Master.FileName.String;
                if (!seen.Add(name)) continue;
                if (!index.TryGetValue(name, out var path))
                {
                    warnings.Add($"Master {name} was not found in the plugin folders, so speakers and voices defined there can't be resolved.");
                    continue;
                }
                try
                {
                    var masterMod = Open(path);
                    Visit(masterMod);   // its own masters first, so load order is right
                    loaded.Add(masterMod);
                }
                catch (Exception ex) { warnings.Add($"Could not read master {name}: {ex.Message}"); }
            }
        }

        try { target = Open(pluginPath); }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Could not read {Path.GetFileName(pluginPath)} as a Skyrim Special Edition plugin: {ex.Message}");
        }
        seen.Add(Path.GetFileName(pluginPath));
        Visit(target);
        loaded.Add(target);
        return loaded;
    }

    public static PluginDialogue Read(string pluginPath, IReadOnlyList<PluginFile> available)
    {
        if (!File.Exists(pluginPath)) throw new InvalidOperationException($"Plugin not found: {pluginPath}");

        var index = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in available) index[p.Name] = p.Path;
        // Masters sit next to the plugin when it is loaded by hand from somewhere else.
        var sibling = Path.GetDirectoryName(Path.GetFullPath(pluginPath))!;
        foreach (var file in Directory.EnumerateFiles(sibling))
            if (PluginExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase)) index.TryAdd(Path.GetFileName(file), file);

        var result = new PluginDialogue { PluginName = Path.GetFileName(pluginPath), PluginPath = pluginPath };
        var mods = LoadWithMasters(pluginPath, index, result.Warnings, out var mod);
        var cache = mods.ToImmutableLinkCache<ISkyrimMod, ISkyrimModGetter>();
        var localized = mod.ModHeader.Flags.HasFlag(SkyrimModHeader.HeaderFlag.Localized);
        int missingText = 0, unknownSpeaker = 0, questless = 0;

        var voices = new VoiceResolver(cache);
        var sceneVoices = SceneVoices(mod, cache, voices);

        foreach (var topic in mod.DialogTopics)
        {
            if (topic.IsDeleted) continue;
            var quest = topic.Quest.IsNull ? null : cache.TryResolve<IQuestGetter>(topic.Quest.FormKey, out var q) ? q : null;
            var questId = quest?.EditorID;
            if (string.IsNullOrWhiteSpace(questId))
            {
                questless++;
                continue; // the file name starts with the quest's EditorID; without one the game can't find the file
            }
            var topicId = topic.EditorID ?? "";
            sceneVoices.TryGetValue(topic.FormKey, out var sceneVoice);

            foreach (var info in topic.Responses)
            {
                if (info.IsDeleted) continue;
                string voiceType; string speakers;
                if (sceneVoice is not null) { voiceType = sceneVoice.VoiceType; speakers = sceneVoice.Speaker; }
                else (voiceType, speakers) = voices.ForInfo(info, quest!);
                if (voiceType.Length == 0)
                {
                    voiceType = "Unknown";
                    unknownSpeaker++;
                }

                List<IDialogResponseGetter> responses;
                try { responses = info.Responses.ToList(); }
                catch (Exception) { missingText++; continue; }

                foreach (var response in responses)
                {
                    string? text;
                    // A localized plugin keeps its text in .STRINGS files; if those can't be read the lookup fails here.
                    try { text = response.Text?.String; }
                    catch (Exception) { missingText++; continue; }
                    if (string.IsNullOrEmpty(text) && localized) missingText++;
                    if (string.IsNullOrWhiteSpace(text)) continue;
                    var name = InfoFileName(questId!, topicId, info.FormKey.ID, response.ResponseNumber);
                    result.Lines.Add(new DialogueLine(name, questId!, topicId, info.FormKey.ID.ToString("X8"), response.ResponseNumber, SanitizeVoiceType(voiceType), speakers, text));
                }
            }
        }

        if (questless > 0) result.Warnings.Add($"{questless} dialogue topic(s) belong to a quest that has no EditorID or is not in the loaded plugins; they were left out (the file name starts with the quest's EditorID).");
        if (unknownSpeaker > 0) result.Warnings.Add($"{unknownSpeaker} line(s) have no speaker the plugin can resolve; they are listed under VoiceType \"Unknown\" -- map or exclude them.");
        if (missingText > 0)
            result.Warnings.Add($"{missingText} line(s) could not be read: this plugin is localized (its text is stored in .STRINGS files) and those files were not found or not readable next to it -- they may be inside a .bsa. Extract the plugin's Strings folder next to it and read it again.");
        if (result.Lines.Count == 0 && missingText == 0)
            result.Warnings.Add("No voiced dialogue was found in this plugin.");
        return result;
    }

    // ---------------------------------------------------------------- who speaks

    private sealed record SceneVoice(string VoiceType, string Speaker);

    /// <summary>A topic that a scene plays is spoken by the scene action's actor alias, whatever the INFOs' own conditions say.</summary>
    private static Dictionary<FormKey, SceneVoice> SceneVoices(ISkyrimModGetter mod, ILinkCache cache, VoiceResolver voices)
    {
        var map = new Dictionary<FormKey, SceneVoice>();
        foreach (var scene in mod.Scenes)
        {
            if (scene.IsDeleted || scene.Quest.IsNull || !cache.TryResolve<IQuestGetter>(scene.Quest.FormKey, out var quest)) continue;
            foreach (var action in scene.Actions)
            {
                if (action.Type != SceneAction.TypeEnum.Dialog || action.Topic.IsNull || action.ActorID is not { } aliasId) continue;
                var (voice, speaker) = voices.ForAlias(quest, (int)aliasId);
                if (voice.Length > 0) map.TryAdd(action.Topic.FormKey, new SceneVoice(voice, speaker));
            }
        }
        return map;
    }

    private sealed class VoiceResolver
    {
        private readonly ILinkCache _cache;
        public VoiceResolver(ILinkCache cache) => _cache = cache;

        /// <summary>The EditorID of the VoiceType a link points at; for a list of voice types, the first one stands for the list.</summary>
        private string VoiceName(FormKey key)
        {
            if (_cache.TryResolve<IVoiceTypeGetter>(key, out var vt)) return vt.EditorID ?? "";
            if (_cache.TryResolve<IFormListGetter>(key, out var list))
                foreach (var item in list.Items)
                    if (_cache.TryResolve<IVoiceTypeGetter>(item.FormKey, out var inList)) return inList.EditorID ?? "";
            return "";
        }

        private string VoiceName<T>(IFormLinkNullableGetter<T> link) where T : class, IMajorRecordGetter => link.IsNull ? "" : VoiceName(link.FormKey);

        /// <summary>The VoiceType of an actor-like record: an NPC (following its template when it inherits traits), a talking activator,
        /// or a placed reference of either.</summary>
        public (string Voice, string Name) OfActor(FormKey key, int depth = 0)
        {
            if (depth > 4) return ("", "");
            if (_cache.TryResolve<IPlacedNpcGetter>(key, out var placedNpc) && !placedNpc.Base.IsNull) return OfActor(placedNpc.Base.FormKey, depth + 1);
            if (_cache.TryResolve<IPlacedObjectGetter>(key, out var placed) && !placed.Base.IsNull) return OfActor(placed.Base.FormKey, depth + 1);
            if (_cache.TryResolve<INpcGetter>(key, out var npc))
            {
                var name = !string.IsNullOrEmpty(npc.Name?.String) ? npc.Name!.String! : npc.EditorID ?? "";
                var own = VoiceName(npc.Voice);
                bool takesTemplateTraits = npc.Configuration.TemplateFlags.HasFlag(NpcConfiguration.TemplateFlag.Traits);
                if ((own.Length == 0 || takesTemplateTraits) && !npc.Template.IsNull)
                {
                    var (fromTemplate, _) = OfActor(npc.Template.FormKey, depth + 1);
                    if (fromTemplate.Length > 0) return (fromTemplate, name);
                }
                return (own, name);
            }
            if (_cache.TryResolve<ITalkingActivatorGetter>(key, out var tact))
                return (VoiceName(tact.Voice), tact.Name?.String ?? tact.EditorID ?? "");
            return ("", "");
        }

        /// <summary>The voice a quest alias stands for: its own VoiceType filter first, then the actor it points at.</summary>
        public (string Voice, string Speaker) ForAlias(IQuestGetter quest, int aliasId)
        {
            var alias = quest.Aliases.FirstOrDefault(a => a.ID == aliasId);
            if (alias is null) return ("", "");
            var direct = VoiceName(alias.VoiceTypes);
            if (direct.Length > 0) return (direct, alias.Name ?? "");
            FormKey? actor = !alias.UniqueActor.IsNull ? alias.UniqueActor.FormKey : !alias.ForcedReference.IsNull ? alias.ForcedReference.FormKey : null;
            if (actor is { } key)
            {
                var (voice, name) = OfActor(key);
                if (voice.Length > 0) return (voice, name.Length > 0 ? name : alias.Name ?? "");
            }
            return ("", alias.Name ?? "");
        }

        /// <summary>Priority, as in the xEdit exporters: the INFO's Speaker, then its own conditions, then the quest's dialogue conditions.</summary>
        public (string Voice, string Speakers) ForInfo(IDialogResponsesGetter info, IQuestGetter quest)
        {
            if (!info.Speaker.IsNull)
            {
                var (voice, name) = OfActor(info.Speaker.FormKey);
                if (voice.Length > 0) return (voice, name);
            }
            var fromInfo = FromConditions(info.Conditions, quest);
            if (fromInfo.Voice.Length > 0) return fromInfo;
            return FromConditions(quest.DialogConditions, quest);
        }

        private (string Voice, string Speakers) FromConditions(IReadOnlyList<IConditionGetter> conditions, IQuestGetter quest)
        {
            static bool IsTrue(IConditionGetter c) => c.CompareOperator == CompareOperator.EqualTo && c is IConditionFloatGetter { ComparisonValue: 1 };

            // GetIsVoiceType beats GetIsAliasRef beats GetIsID, as separate passes, so condition order can't change the answer.
            foreach (var c in conditions.Where(IsTrue))
                if (c.Data is IGetIsVoiceTypeConditionDataGetter isVoice && VoiceName(isVoice.VoiceTypeOrList.Link) is { Length: > 0 } voice) return (voice, "");
            foreach (var c in conditions.Where(IsTrue))
                if (c.Data is IGetIsAliasRefConditionDataGetter isAlias)
                {
                    var (voice, speaker) = ForAlias(quest, isAlias.ReferenceAliasIndex);
                    if (voice.Length > 0) return (voice, speaker);
                }
            foreach (var c in conditions.Where(IsTrue))
            {
                if (c.Data is IGetIsIDConditionDataGetter isId && !isId.Object.Link.IsNull)
                {
                    var (voice, name) = OfActor(isId.Object.Link.FormKey);
                    if (voice.Length > 0) return (voice, name);
                }
                else if (c.Data is IGetIsReferenceConditionDataGetter isRef && !isRef.Target.Link.IsNull)
                {
                    var (voice, name) = OfActor(isRef.Target.Link.FormKey);
                    if (voice.Length > 0) return (voice, name);
                }
            }
            return ("", "");
        }
    }
}
