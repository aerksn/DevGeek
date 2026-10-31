using System.Windows.Markup;
using OtterMonitor.Core;

namespace OtterMonitor.App;

[MarkupExtensionReturnType(typeof(string))]
public sealed class LocExtension(string text) : MarkupExtension
{
    public override object ProvideValue(IServiceProvider serviceProvider) => UiText.T(text);
}
