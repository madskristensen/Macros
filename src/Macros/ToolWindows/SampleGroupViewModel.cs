namespace Macros.ToolWindows;

public sealed class SampleGroupViewModel : ExpandableGroupViewModel<SampleTemplateItemViewModel>
{
    public SampleGroupViewModel(string header)
        : base(header, isExpanded: false)
    {
    }
}
