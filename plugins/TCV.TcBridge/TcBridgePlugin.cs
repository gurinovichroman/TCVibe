using TCV.Core.PluginSdk;

namespace TCV.TcBridge;

/// <summary>Placeholder for Этап 3 — Total Commander native plugin bridge (WFX/WCX/WDX).</summary>
public class TcBridgePlugin : IPlugin
{
    public string Id => "tcv.tcbridge";
    public string Name => "TC Bridge";
    public Version Version => new(0, 1, 0);

    public void Initialize(IPluginHost host)
    {
        // Placeholder — will register WFX/WCX/WDX adapters in Этап 3
    }
}
