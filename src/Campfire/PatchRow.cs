using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using Campfire.Launcher;
using Campfire.Localization;
using Campfire.Patching;

namespace Campfire
{
    /// <summary>
    /// Display model of one patch in the "Repairs" card. Rebuilt whenever the display language changes.
    /// </summary>
    internal sealed class PatchRow : INotifyPropertyChanged
    {
        private static readonly Geometry CheckIcon = Geometry.Parse("M3,8.5 L6.5,12 L13,4.5");
        private static readonly Geometry PendingIcon = Geometry.Parse("M8,4 L8,9 M8,11.5 L8,12");
        private static readonly Geometry UnknownIcon = Geometry.Parse("M4.5,8 L11.5,8");
        private bool _isChecked;

        private PatchRow(string key, string title, string description, string stateText, Brush accent, Geometry icon,
            bool isSelectable, bool isChecked)
        {
            Key = key;
            Title = title;
            Description = description;
            StateText = stateText;
            Accent = accent;
            Icon = icon;
            IsSelectable = isSelectable;
            _isChecked = isChecked;
        }

        public event PropertyChangedEventHandler PropertyChanged;

        public string Key { get; }

        public string Title { get; }

        public string Description { get; }

        public string StateText { get; }

        public Brush Accent { get; }

        public Geometry Icon { get; }

        public bool IsSelectable { get; }

        public bool IsChecked
        {
            get => _isChecked;
            set
            {
                if (_isChecked == value)
                    return;
                _isChecked = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked)));
            }
        }

        public static PatchRow From(PatchReport report, LauncherStatus launcherStatus)
        {
            string stateText;
            switch (report.State)
            {
                case PatchState.Applied:
                    stateText = launcherStatus == LauncherStatus.Patched ? report.AppliedText : Text.Get("PatchState.Normal");
                    break;
                case PatchState.Pending:
                    stateText = Text.Get("PatchState.Pending");
                    break;
                default:
                    stateText = Text.Get(launcherStatus == LauncherStatus.NotBlocked ? "PatchState.NotNeeded" : "PatchState.NotFound");
                    break;
            }
            bool isSelectable = launcherStatus == LauncherStatus.Blocked && report.State == PatchState.Pending;
            bool isChecked = report.State == PatchState.Applied || isSelectable;
            return Create(report.Title, report.Description, stateText, report.State,
                isSelectable, isChecked, report.Id);
        }

        /// <summary>A row for a tool that is not a patch; the colour and icon follow <paramref name="state"/>.</summary>
        public static PatchRow Create(string title, string description, string stateText, PatchState state,
            bool isSelectable = false, bool isChecked = false, string key = null)
        {
            switch (state)
            {
                case PatchState.Applied:
                    return new PatchRow(key, title, description, stateText, Resource("BrushGreen"), CheckIcon,
                        isSelectable, isChecked);
                case PatchState.Pending:
                    return new PatchRow(key, title, description, stateText, Resource("Brush3"), PendingIcon,
                        isSelectable, isChecked);
                default:
                    return new PatchRow(key, title, description, stateText, Resource("BrushGray4"), UnknownIcon,
                        isSelectable, isChecked);
            }
        }

        /// <summary>Placeholder rows shown before a launcher is selected.</summary>
        public static PatchRow Placeholder(Patch patch)
        {
            return new PatchRow(patch.Id, patch.Title, patch.Description, "—", Resource("BrushGray5"), UnknownIcon,
                false, false);
        }

        private static Brush Resource(string key)
        {
            // Theme colors live on the main window.
            return (Brush)(Application.Current.MainWindow?.TryFindResource(key) ?? Application.Current.FindResource(key));
        }
    }
}
