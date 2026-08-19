using Robust.Client.UserInterface;

namespace Content.Client._ErisWS.DEM.UI;

public sealed class DEMConsoleBoundUserInterface(EntityUid owner, Enum uiKey) : BoundUserInterface(owner, uiKey)
{
    protected override void Open()
    {
        base.Open();
        this.CreateWindow<DEMConsoleWindow>();
    }
}
