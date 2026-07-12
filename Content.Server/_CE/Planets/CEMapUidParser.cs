/*
 * This file is sublicensed under MIT License
 * https://github.com/space-wizards/space-station-14/blob/master/LICENSE.TXT
 */

using Robust.Shared.Console;
using Robust.Shared.Map;
using Robust.Shared.Toolshed;
using Robust.Shared.Toolshed.Errors;
using Robust.Shared.Toolshed.Syntax;
using Robust.Shared.Toolshed.TypeParsers;
using Robust.Shared.Utility;

namespace Content.Server._CE.Planets;

/// <summary>
/// Toolshed arg parser that takes a <see cref="MapId"/> (the integer you see in <c>savemap</c>)
/// and resolves it to the map's entity, autocompleting existing maps the same way <c>savemap</c>
/// does. Used by <c>cespawnplanet</c> so you point at a map by its id, not an arbitrary entity uid.
/// </summary>
public sealed partial class CEMapUidParser : CustomTypeParser<EntityUid>
{
    [Dependency] private IEntityManager _entManager = default!;
    [Dependency] private IMapManager _mapManager = default!;

    public override bool TryParse(ParserContext ctx, out EntityUid result)
    {
        result = default;

        var word = ctx.GetWord(ParserContext.IsToken);
        if (word is not null
            && int.TryParse(word, out var intId))
        {
            var mapId = new MapId(intId);
            if (mapId != MapId.Nullspace && _mapManager.MapExists(mapId))
            {
                result = _mapManager.GetMapEntityId(mapId);
                return true;
            }
        }

        ctx.Error = new NotAMap(word ?? "[null]");
        return false;
    }

    public override CompletionResult TryAutocomplete(ParserContext ctx, CommandArgument? arg)
    {
        return CompletionResult.FromHintOptions(
            CompletionHelper.MapIds(_entManager),
            ToolshedCommand.GetArgHint(arg, typeof(EntityUid)));
    }
}

public sealed class NotAMap(string value) : ConError
{
    public override FormattedMessage DescribeInner()
    {
        return FormattedMessage.FromUnformatted($"{value} is not the id of an existing map");
    }
}
