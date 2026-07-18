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
/// Toolshed arg parser that accepts only <see cref="CEZPlanetPrototype"/> ids (cezPlanet
/// prototypes), so <c>cezspawnplanet</c> autocompletes the available z-stack layouts.
/// </summary>
public sealed partial class CEZPlanetProtoParser : CustomTypeParser<ProtoId<CEZPlanetPrototype>>
{
    [Dependency] private IPrototypeManager _proto = default!;

    public override bool TryParse(ParserContext ctx, out ProtoId<CEZPlanetPrototype> result)
    {
        result = default;

        var word = ctx.GetWord(ParserContext.IsToken);
        if (word is not null && _proto.HasIndex<CEZPlanetPrototype>(word))
        {
            result = new ProtoId<CEZPlanetPrototype>(word);
            return true;
        }

        ctx.Error = new NotAZPlanetPrototype(word ?? "[null]");
        return false;
    }

    public override CompletionResult TryAutocomplete(ParserContext ctx, CommandArgument? arg)
    {
        var options = _proto.EnumeratePrototypes<CEZPlanetPrototype>()
            .Select(p => new CompletionOption(p.ID));

        return CompletionResult.FromHintOptions(
            options,
            ToolshedCommand.GetArgHint(arg, typeof(ProtoId<CEZPlanetPrototype>)));
    }
}

public sealed class NotAZPlanetPrototype(string proto) : ConError
{
    public override FormattedMessage DescribeInner()
    {
        return FormattedMessage.FromUnformatted($"{proto} is not a cezPlanet (CEZPlanetPrototype) id");
    }
}
