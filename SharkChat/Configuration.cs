using System;
using System.Collections.Generic;
using Dalamud.Configuration;

namespace SharkChat;

[Serializable]
public class SubstitutionRule
{
    /// <summary>The word or phrase to look for in outgoing chat.</summary>
    public string From { get; set; } = string.Empty;

    /// <summary>What it should be replaced with.</summary>
    public string To { get; set; } = string.Empty;

    /// <summary>Only match whole words — prevents "tank" matching inside "thanks".</summary>
    public bool WholeWordOnly { get; set; } = true;

    /// <summary>Whether the match is case-sensitive.</summary>
    public bool CaseSensitive { get; set; } = false;

    /// <summary>Quickly disable a rule without deleting it.</summary>
    public bool Enabled { get; set; } = true;
}

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 0;

    /// <summary>Master switch — when false, no substitutions are applied.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>The list of word-swap rules, applied in order.</summary>
    public List<SubstitutionRule> Rules { get; set; } = new();

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
