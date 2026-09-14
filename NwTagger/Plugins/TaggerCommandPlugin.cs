using Autodesk.Navisworks.Api;
using Autodesk.Navisworks.Api.Plugins;
using NwTagger.Core;

namespace NwTagger.Plugins
{
    /// <summary>
    /// Ribbon button under Tool Add-ins that shows or hides the tagger panel.
    /// </summary>
    [Plugin("NwTagger.Show", "ABHM",
        DisplayName = "Element Tagger",
        ToolTip = "Show or hide the Element Tagger panel.")]
    [AddInPlugin(AddInLocation.AddIn,
        CanToggle = true,
        LoadForCanExecute = true)]
    public sealed class TaggerCommandPlugin : AddInPlugin
    {
        public override int Execute(params string[] parameters)
        {
            ToolController.SetPaneVisible(!ToolController.IsPaneVisible());
            return 0;
        }

        public override CommandState CanExecute()
        {
            CommandState state = new CommandState(true);

            state.IsVisible = true;
            state.IsChecked = ToolController.IsPaneVisible();
            state.IsEnabled = Application.ActiveDocument != null;

            return state;
        }
    }
}
