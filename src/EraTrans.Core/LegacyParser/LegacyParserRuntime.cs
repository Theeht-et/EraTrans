using System.Text;

namespace EraTrans.Core;

internal static class LegacyParserRuntime
{
    internal static readonly object SyncRoot = new();

    internal static void Initialize(EraParserOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Configs.Apply(options);
        Tools.Init();
    }
}