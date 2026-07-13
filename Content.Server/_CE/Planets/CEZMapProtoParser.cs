/*
 * This file is sublicensed under MIT License
 * https://github.com/space-wizards/space-station-14/blob/master/LICENSE.TXT
 */

using System.Linq;
using Content.Shared._CE.ZLevels.Mapping.Prototypes;
using Robust.Shared.Console;
using Robust.Shared.Prototypes;
using Robust.Shared.Toolshed;
using Robust.Shared.Toolshed.Errors;
using Robust.Shared.Toolshed.Syntax;
using Robust.Shared.Toolshed.TypeParsers;
using Robust.Shared.Utility;

namespace Content.Server._CE.Planets;

/// <summary>
/// Toolshed arg parser that accepts only <see cref="CEZLevelMapPrototype"/> ids (zMap prototypes),
/// so <c>cezspawnplanet</c> autocompletes the available z-stack layouts (Grasslands, Empty5, ...).
/// </summary>
public sealed partial class CEZMapProtoParser : CustomTypeParser<ProtoId<CEZLevelMapPrototype>>
{
    [Dependency] private IPrototypeManager _proto = default!;

    public override bool TryParse(ParserContext ctx, out ProtoId<CEZLevelMapPrototype> result)
    {
        result = default;

        var word = ctx.GetWord(ParserContext.IsToken);
        if (word is not null && _proto.HasIndex<CEZLevelMapPrototype>(word))
        {
            result = new ProtoId<CEZLevelMapPrototype>(word);
            return true;
        }

        ctx.Error = new NotAZMapPrototype(word ?? "[null]");
        return false;
    }

    public override CompletionResult TryAutocomplete(ParserContext ctx, CommandArgument? arg)
    {
        var options = _proto.EnumeratePrototypes<CEZLevelMapPrototype>()
            .Select(p => new CompletionOption(p.ID));

        return CompletionResult.FromHintOptions(
            options,
            ToolshedCommand.GetArgHint(arg, typeof(ProtoId<CEZLevelMapPrototype>)));
    }
}

public sealed class NotAZMapPrototype(string proto) : ConError
{
    public override FormattedMessage DescribeInner()
    {
        return FormattedMessage.FromUnformatted($"{proto} is not a zMap (CEZLevelMapPrototype) id");
    }
}
