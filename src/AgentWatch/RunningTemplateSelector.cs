using System.Windows;
using System.Windows.Controls;

namespace AgentWatch;

/// <summary>Picks the card template for the mixed "Running now" list (session activity + commands).</summary>
public sealed class RunningTemplateSelector : DataTemplateSelector
{
    public DataTemplate? ActivityTemplate { get; set; }
    public DataTemplate? TaskTemplate { get; set; }

    public override DataTemplate? SelectTemplate(object item, DependencyObject container) =>
        item is SessionVm ? ActivityTemplate : TaskTemplate;
}
