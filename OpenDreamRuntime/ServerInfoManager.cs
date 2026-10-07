using System.Text.Json.Nodes;
using OpenDreamShared;
using Robust.Server.ServerStatus;
using Robust.Shared.Configuration;

namespace OpenDreamRuntime;

/// <summary>
///     Adds additional data like info links to the server info endpoint
/// </summary>
public sealed partial class ServerInfoManager {
    private static readonly (CVarDef<string> cVar, string icon, string name)[] Vars = {
        // @formatter:off
        (OpenDreamCVars.InfoLinksDiscord, "discord", "Discord"),
        (OpenDreamCVars.InfoLinksForum,   "forum",   "Forum"),
        (OpenDreamCVars.InfoLinksGithub,  "github",  "GitHub"),
        (OpenDreamCVars.InfoLinksWebsite, "web",     "Website"),
        (OpenDreamCVars.InfoLinksWiki,    "wiki",    "Wiki")
        // @formatter:on
    };

    [Dependency] private IConfigurationManager _cfg = default!;

    [Dependency] private IStatusHost _statusHost = default!;

    public void Initialize() {
        _statusHost.OnInfoRequest += OnInfoRequest;
    }

    private void OnInfoRequest(JsonNode json) {
        foreach ((CVarDef<string> cVar, string icon, string name) in Vars) {
            string url = _cfg.GetCVar(cVar);
            if (string.IsNullOrEmpty(url))
                continue;

            StatusHostHelpers.AddLink(json, name, url, icon);
        }
    }
}
