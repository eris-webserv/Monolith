/*
 * This file is sublicensed under MIT License
 * https://github.com/space-wizards/space-station-14/blob/master/LICENSE.TXT
 */

using System.Linq;
using Content.Shared._CE.Planets;
using Robust.Shared.Console;
using Robust.Shared.Prototypes;
using Robust.Shared.Toolshed;
using Robust.Shared.Toolshed.Errors;
using Robust.Shared.Toolshed.Syntax;
using Robust.Shared.Toolshed.TypeParsers;
using Robust.Shared.Utility;

namespace Content.Server._CE.Planets;

/// <summary>
/// Toolshed arg parser that accepts only entity prototypes carrying <see cref="CEPlanetComponent"/>,
/// so <c>cespawnplanet</c> autocompletes just planets instead of every entity in the game.
/// </summary>
public sealed partial class CEPlanetProtoParser : CustomTypeParser<EntProtoId>
{
    [Dependency] private IPrototypeManager _proto = default!;
    [Dependency] private IComponentFactory _factory = default!;

    private string PlanetCompName => _factory.GetComponentName<CEPlanetComponent>();

    private bool IsPlanet(EntityPrototype proto) => proto.Components.ContainsKey(PlanetCompName);

    public override bool TryParse(ParserContext ctx, out EntProtoId result)
    {
        result = default;

        var word = ctx.GetWord(ParserContext.IsToken);
        if (word is not null
            && _proto.TryIndex<EntityPrototype>(word, out var proto)
            && IsPlanet(proto))
        {
            result = new EntProtoId(word);
            return true;
        }

        ctx.Error = new NotAPlanetPrototype(word ?? "[null]");
        return false;
    }

    public override CompletionResult TryAutocomplete(ParserContext ctx, CommandArgument? arg)
    {
        var options = _proto.EnumeratePrototypes<EntityPrototype>()
            .Where(IsPlanet)
            .Select(p => new CompletionOption(p.ID, p.Name));

        return CompletionResult.FromHintOptions(options, ToolshedCommand.GetArgHint(arg, typeof(EntProtoId)));
    }
}

public sealed class NotAPlanetPrototype(string proto) : ConError
{
    public override FormattedMessage DescribeInner()
    {
        return FormattedMessage.FromUnformatted($"{proto} is not a planet prototype (needs a CEPlanet component)");
    }
}
