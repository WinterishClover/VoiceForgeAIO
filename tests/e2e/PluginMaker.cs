using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Mutagen.Bethesda.Plugins.Binary.Parameters;
using Mutagen.Bethesda.Strings;
using Mutagen.Bethesda.Strings.DI;
using Mutagen.Bethesda.Plugins.Exceptions;

namespace VoiceForgeAIO;

/// <summary>Writes a small master + plugin pair that exercises every speaker rule the Plugin Dialogue reader implements.</summary>
internal static class PluginMaker
{
    static DialogResponses Info(SkyrimMod mod, DialogTopic topic, string? editorId, params string[] texts)
    {
        var info = new DialogResponses(mod.GetNextFormKey(), SkyrimRelease.SkyrimSE) { EditorID = editorId };
        for (int i = 0; i < texts.Length; i++) info.Responses.Add(new DialogResponse { ResponseNumber = (byte)(i + 1), Text = texts[i] });
        topic.Responses.Add(info);
        return info;
    }

    static DialogTopic Topic(SkyrimMod mod, IQuestGetter quest, string editorId)
    {
        var topic = new DialogTopic(mod.GetNextFormKey(), SkyrimRelease.SkyrimSE) { EditorID = editorId };
        topic.Quest.SetTo(quest);
        mod.DialogTopics.Add(topic);
        return topic;
    }

    static ConditionFloat Cond<T>(T data, float value = 1) where T : ConditionData => new() { CompareOperator = CompareOperator.EqualTo, ComparisonValue = value, Data = data };

    public static void Make(string dir)
    {
        Directory.CreateDirectory(dir);
        // ---- master: voices, NPCs and a quest the plugin adds lines to
        var master = new SkyrimMod(ModKey.FromNameAndExtension("TestBase.esm"), SkyrimRelease.SkyrimSE);
        master.ModHeader.Flags |= SkyrimModHeader.HeaderFlag.Master;
        var guardVoice = master.VoiceTypes.AddNew("MaleGuard");
        var mageVoice = master.VoiceTypes.AddNew("FemaleMage");
        var guard = master.Npcs.AddNew("BaseGuard"); guard.Name = "Whiterun Guard"; guard.Voice.SetTo(guardVoice);
        var mage = master.Npcs.AddNew("BaseMage"); mage.Name = "Court Mage"; mage.Voice.SetTo(mageVoice);
        var baseQuest = master.Quests.AddNew("BaseQuest");
        master.WriteToBinary(Path.Combine(dir, "TestBase.esm"));

        // ---- plugin
        var mod = new SkyrimMod(ModKey.FromNameAndExtension("TestMod.esp"), SkyrimRelease.SkyrimSE);
        var modVoice = mod.VoiceTypes.AddNew("ModVoice");
        var childVoice = mod.VoiceTypes.AddNew("ChildVoice");
        var hero = mod.Npcs.AddNew("ModHero"); hero.Name = "The Hero"; hero.Voice.SetTo(modVoice);
        // An NPC with no voice of its own that takes its traits from a template: speaks with the template's voice.
        var twin = mod.Npcs.AddNew("MageTwin"); twin.Name = "Mage Twin"; twin.Template.SetTo(mage);
        twin.Configuration.TemplateFlags |= NpcConfiguration.TemplateFlag.Traits;

        var quest = mod.Quests.AddNew("TQ");
        var a0 = new QuestAlias { ID = 0, Name = "TwinAlias" }; a0.UniqueActor.SetTo(twin); quest.Aliases.Add(a0);
        var a1 = new QuestAlias { ID = 1, Name = "ChildAlias" }; a1.VoiceTypes.SetTo(childVoice.FormKey); quest.Aliases.Add(a1);

        var greeting = Topic(mod, quest, "Greeting");
        var bySpeaker = Info(mod, greeting, "InfoSpeaker", "Halt! Who goes there?", "State your business.");
        bySpeaker.Speaker.SetTo(guard);
        var byVoiceCond = Info(mod, greeting, null, "By voice type.");
        byVoiceCond.Conditions.Add(Cond(new GetIsVoiceTypeConditionData { VoiceTypeOrList = { Link = { FormKey = modVoice.FormKey } } }));
        var byAlias = Info(mod, greeting, null, "By alias, template voice.");
        byAlias.Conditions.Add(Cond(new GetIsAliasRefConditionData { ReferenceAliasIndex = 0 }));
        var byId = Info(mod, greeting, null, "By GetIsID, master NPC.");
        byId.Conditions.Add(Cond(new GetIsIDConditionData { Object = { Link = { FormKey = mage.FormKey } } }));
        Info(mod, greeting, null, "Nobody can say this.");                       // no speaker at all -> Unknown
        Info(mod, greeting, null, "   ").Speaker.SetTo(guard);                    // blank text: skipped
        var notTrue = Info(mod, greeting, null, "Only when NOT the mage.");
        notTrue.Conditions.Add(Cond(new GetIsIDConditionData { Object = { Link = { FormKey = mage.FormKey } } }, 0)); // ==0 must not count

        // Quest-level conditions are the last resort
        var qquest = mod.Quests.AddNew("QuestCond");
        qquest.DialogConditions.Add(Cond(new GetIsVoiceTypeConditionData { VoiceTypeOrList = { Link = { FormKey = guardVoice.FormKey } } }));
        var qtopic = Topic(mod, qquest, "Idle");
        Info(mod, qtopic, null, "Falls back to the quest's conditions.");

        // Long names: quest 2 + topic 26 -> over 25, so the topic gets 23
        Info(mod, Topic(mod, quest, "AVeryLongTopicEditorIDHere1"), null, "Long topic.").Speaker.SetTo(hero);
        // Quest 20 + topic 4 = 24 <= 25 -> nothing is shortened
        var longQuest = mod.Quests.AddNew("TheLongQuestEditorID");
        Info(mod, Topic(mod, longQuest, "Chat"), null, "Long quest, short topic.").Speaker.SetTo(hero);
        // Quest 20 + topic 15 = 35 -> quest to 10, topic to 15
        Info(mod, Topic(mod, longQuest, "AlsoQuiteLongTopic"), null, "Both long.").Speaker.SetTo(hero);

        // A topic played by a scene: the scene action's alias decides the voice, not the INFO
        var sceneTopic = Topic(mod, quest, "SceneTalk");
        Info(mod, sceneTopic, null, "Said in a scene.").Speaker.SetTo(guard);
        var scene = new Scene(mod.GetNextFormKey(), SkyrimRelease.SkyrimSE) { EditorID = "TestScene" };
        scene.Quest.SetTo(quest);
        scene.Actions.Add(new SceneAction { Type = SceneAction.TypeEnum.Dialog, ActorID = 1, Index = 0 });
        scene.Actions[0].Topic.SetTo(sceneTopic);
        mod.Scenes.Add(scene);

        // A line added to a master's quest: the quest's EditorID comes from the master
        var onMaster = Topic(mod, baseQuest, "ExtraLine");
        Info(mod, onMaster, null, "On a master's quest.").Speaker.SetTo(mage);

        // Deleted topic is ignored
        var deleted = Topic(mod, quest, "Gone"); deleted.IsDeleted = true; Info(mod, deleted, null, "Deleted.");

        mod.WriteToBinary(Path.Combine(dir, "TestMod.esp"));
    }

    /// <summary>A localized plugin: its dialogue text is written to Strings\\*.STRINGS files next to it instead of into the plugin.</summary>
    public static void MakeLocalized(string dir)
    {
        var mod = new SkyrimMod(ModKey.FromNameAndExtension("LocMod.esp"), SkyrimRelease.SkyrimSE) { UsingLocalization = true };
        var voice = mod.VoiceTypes.AddNew("LocVoice");
        var npc = mod.Npcs.AddNew("LocNpc"); npc.Voice.SetTo(voice);
        var quest = mod.Quests.AddNew("LocQuest");
        var topic = Topic(mod, quest, "LocTopic");
        var info = new DialogResponses(mod.GetNextFormKey(), SkyrimRelease.SkyrimSE);
        info.Speaker.SetTo(npc);
        info.Responses.Add(new DialogResponse { ResponseNumber = 1, Text = new TranslatedString(Language.English, "Localized words.") });
        topic.Responses.Add(info);
        mod.WriteToBinary(Path.Combine(dir, "LocMod.esp"), new BinaryWriteParameters { StringsWriter = new StringsWriter(GameRelease.SkyrimSE, mod.ModKey, Path.Combine(dir, "Strings"), MutagenEncoding.Default) });
    }
}
