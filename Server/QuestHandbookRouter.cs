using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Utils;

namespace QuestHandbook
{
    // Static route consumed by the QuestHandbook BepInEx client plugin.
    [Injectable(TypePriority = OnLoadOrder.Routers + 1)]
    internal class QuestHandbookRouter(
        JsonUtil jsonUtil,
        QuestHandbookMod mod
    ) : StaticRouter(
        jsonUtil,
        [
            new RouteAction<EmptyRequestData>(
                "/quest-book/quests",
                (url, info, sessionId, output, cancellationToken) => mod.HandleQuestBook()
            ),
            new RouteAction<EmptyRequestData>(
                "/quest-book/recipes",
                (url, info, sessionId, output, cancellationToken) => mod.HandleRecipeBook()
            ),
            new RouteAction<EmptyRequestData>(
                "/quest-book/construction",
                (url, info, sessionId, output, cancellationToken) => mod.HandleConstructionBook()
            ),
        ]
    );
}
