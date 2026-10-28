using System.Windows.Forms;
using Tedd.Defrag.Update;

namespace Tedd.Defrag.Cli;

internal static class UpdateProgressWindow
{
    public static Task<int> Run(Func<IProgress<UpdateProgress>, Task<int>> apply)
    {
        var completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                int exitCode = 1;
                bool finished = false;
                using var window = new Form
                {
                    Text = "Tedd.Defrag update",
                    Width = 520,
                    Height = 180,
                    MinimumSize = new(520, 180),
                    StartPosition = FormStartPosition.CenterScreen,
                    FormBorderStyle = FormBorderStyle.FixedDialog,
                    MaximizeBox = false,
                    MinimizeBox = false
                };
                var status = new Label { Left = 20, Top = 20, Width = 460, Height = 42, Text = "Preparing update…" };
                var bar = new ProgressBar { Left = 20, Top = 70, Width = 460, Height = 20, Style = ProgressBarStyle.Marquee };
                var close = new Button { Left = 395, Top = 102, Width = 85, Text = "Close", Enabled = false };
                close.Click += (_, _) => window.Close();
                window.FormClosing += (_, args) => { if (!finished) args.Cancel = true; };
                window.Controls.AddRange([status, bar, close]);
                window.Shown += async (_, _) =>
                {
                    var progress = new Progress<UpdateProgress>(update =>
                    {
                        if (window.IsDisposed) return;
                        status.Text = update.Message;
                        if (update.Fraction is { } fraction)
                        {
                            bar.Style = ProgressBarStyle.Continuous;
                            bar.Value = Math.Clamp((int)(fraction * 100), 0, 100);
                        }
                        else bar.Style = ProgressBarStyle.Marquee;
                    });
                    try
                    {
                        exitCode = await Task.Run(() => apply(progress));
                        status.Text = exitCode == 0 ? "Update installed." : "Update failed.";
                    }
                    catch (Exception exception)
                    {
                        status.Text = "Update failed. The installed version was not confirmed.";
                        MessageBox.Show(window, exception.Message, "Tedd.Defrag update failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                    finished = true;
                    if (exitCode == 0) window.Close();
                    else close.Enabled = true;
                };
                Application.Run(window);
                completion.TrySetResult(exitCode);
            }
            catch (Exception exception) { completion.TrySetException(exception); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }
}
