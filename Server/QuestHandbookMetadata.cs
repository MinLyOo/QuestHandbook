using SPTarkov.Server.Core.Models.Spt.Mod;

namespace QuestHandbook
{
    // Server mod metadata (SPT 4.1 style, replaces the legacy package.json).
    public record QuestHandbookMetadata : IModMetadata
    {
        public string ModGuid { get; init; } = "com.kee.questhandbook";
        public string Name { get; init; } = "QuestHandbook";
        public string Author { get; init; } = "kee";
        public List<string>? Contributors { get; init; }
        public SemanticVersioning.Version Version { get; init; } = new("0.1.0");
        public SemanticVersioning.Range SptVersion { get; init; } = new("~4.1.0");
        public List<string>? Incompatibilities { get; init; }
        public Dictionary<string, SemanticVersioning.Range>? ModDependencies { get; init; }
        public string? Url { get; init; }
        public string License { get; init; } = "GNU GPLv3";
        public bool HasPrepatcher { get; init; } = false;
    }
}
