using System;
using System.Windows.Forms;
using Autodesk.Navisworks.Api.Plugins;
using NwTagger.Core;

// Autodesk.Navisworks.Api has its own Application and Cursor, so the two used
// here are named explicitly rather than pulling the whole namespace in.
using NwApplication = Autodesk.Navisworks.Api.Application;
using NwDocument = Autodesk.Navisworks.Api.Document;

namespace NwTagger.Plugins
{
    /// <summary>
    /// "AB Tagger - Self test" under Tool Add-ins: places a few tags by itself and
    /// reports whether the markup really ended up in the document.
    ///
    /// It is here because the one thing that cannot be checked by eye is whether a
    /// viewpoint kept its markup - what is drawn on screen is not proof that
    /// anything was stored. See <see cref="TaggerSelfTest"/> for what it does and
    /// what it puts back.
    ///
    /// Not on the ribbon on purpose: it is a diagnostic for when something looks
    /// wrong, not part of tagging.
    /// </summary>
    [Plugin("NwTagger.SelfTest", "ABHM",
        DisplayName = "AB Tagger - Self test",
        ToolTip = "Checks that tag markup is really stored in its viewpoint, and puts everything back.")]
    [AddInPlugin(AddInLocation.AddIn, LoadForCanExecute = true)]
    public sealed class TaggerSelfTestPlugin : AddInPlugin
    {
        public override int Execute(params string[] parameters)
        {
            // ABTAGGER_SELFTEST_QUIET=1 runs it without the dialogs, for an
            // unattended run; the report file is written either way.
            bool quiet = string.Equals(
                Environment.GetEnvironmentVariable("ABTAGGER_SELFTEST_QUIET"), "1", StringComparison.Ordinal);

            NwDocument doc = NwApplication.ActiveDocument;

            if (doc == null || doc.IsClear)
            {
                if (!quiet)
                {
                    MessageBox.Show(
                        "Open a model first - the self test needs elements to tag.",
                        "AB Tagger self test", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }

                return 0;
            }

            if (!quiet)
            {
                DialogResult answer = MessageBox.Show(
                    "The self test will place a few tags in this model, check that their markup is "
                        + "really stored, then remove the viewpoints it created and put the camera, "
                        + "the tag list and the tool back as they were.\n\n"
                        + "It takes a few seconds. Nothing is saved to the model file unless you save it.\n\n"
                        + "Run it now?",
                    "AB Tagger self test", MessageBoxButtons.YesNo, MessageBoxIcon.Question);

                if (answer != DialogResult.Yes) return 0;
            }

            Cursor previous = Cursor.Current;
            Cursor.Current = Cursors.WaitCursor;

            TaggerSelfTest.Result result;

            try
            {
                result = TaggerSelfTest.Run(true);
            }
            finally
            {
                Cursor.Current = previous;
            }

            if (quiet) return result != null && result.Passed ? 0 : 1;

            string message = result.Summary;

            if (!string.IsNullOrEmpty(result.ReportPath))
                message += "\n\nReport:\n" + result.ReportPath;

            MessageBox.Show(
                message,
                "AB Tagger self test",
                MessageBoxButtons.OK,
                result.Passed ? MessageBoxIcon.Information : MessageBoxIcon.Warning);

            return result.Passed ? 0 : 1;
        }

        public override CommandState CanExecute()
        {
            CommandState state = new CommandState(true);

            state.IsVisible = true;
            NwDocument active = NwApplication.ActiveDocument;
            state.IsEnabled = active != null && !active.IsClear;

            return state;
        }
    }
}
