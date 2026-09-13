using System.Collections.Generic;
using KhaozEngine.App;
using KhaozEngine.Windowing;

namespace KhaozEngine.Gui
{
    public sealed partial class RadialMenu
    {
        readonly List<ContextMenuEntry> _entryContextEntries = new(MaximumChoiceCount + 1);
        ContextMenu? _entryContextMenu;
        LocalizedText _quickSelectLabel;
        string _resolvedQuickSelectLabel = "";
        int _contextEntryIndex = -1;
        int _quickSelectPreviewIndex = -1;

        /// <summary>
        /// An optional caller-owned context menu used for right-click entry shortcuts. Configure its viewport and
        /// visual style before assigning it. A menu built with <see cref="KhaozEngine.Render2D.SpriteFont"/> can
        /// be drawn by <see cref="Draw"/>. A measure-only menu supports headless update and retains its normal draw
        /// exception. Replacing this property closes the previous menu.
        /// </summary>
        public ContextMenu? EntryContextMenu
        {
            get => _entryContextMenu;
            set
            {
                if (ReferenceEquals(_entryContextMenu, value))
                    return;
                _entryContextMenu?.Close();
                _entryContextMenu = value;
                _contextEntryIndex = -1;
            }
        }

        /// <summary>
        /// Localized label for the final context row that commits an entry with its retained choice. The label is
        /// resolved once by the next <c>Open</c> call. An empty label omits that row while keeping choice rows.
        /// </summary>
        public LocalizedText QuickSelectLabel
        {
            get => _quickSelectLabel;
            set => _quickSelectLabel = value;
        }

        /// <summary>The quick-select label resolved and retained by the most recent <c>Open</c> call.</summary>
        public string ResolvedQuickSelectLabel => _resolvedQuickSelectLabel;

        internal int QuickSelectPreviewIndex => _quickSelectPreviewIndex;
        internal int QuickSelectPreviewChoiceIndex =>
            CanQuickSelectEntry(_quickSelectPreviewIndex) && _choices.Length > 0
                ? ChoiceIndexForEntry(_quickSelectPreviewIndex)
                : -1;

        void ResolveShortcutLabels() => _resolvedQuickSelectLabel = _quickSelectLabel.Resolve() ?? "";

        bool OpenEntryContextMenu(Pointer pointer)
        {
            ContextMenu? context = _entryContextMenu;
            if (context is null || _choices.Length == 0 || !pointer.IsRightTapIn(Bounds))
                return false;

            int pressedEntry = EntryAt(pointer.RightPressOrigin, _center, _entries.Length, Metrics);
            int releasedEntry = EntryAt(pointer.Position, _center, _entries.Length, Metrics);
            if (pressedEntry < 0 || pressedEntry != releasedEntry || !_entries[releasedEntry].Enabled)
                return false;

            _entryContextEntries.Clear();
            for (int i = 0; i < _choices.Length; i++)
            {
                ResolvedRadialMenuChoice choice = _choices[i];
                _entryContextEntries.Add(new ContextMenuEntry(choice.Content, Tag: choice.Tag, Enabled: choice.Enabled));
            }

            int rememberedChoice = ChoiceIndexForEntry(releasedEntry);
            if (_resolvedQuickSelectLabel.Length > 0)
            {
                string detail = rememberedChoice >= 0 ? _choices[rememberedChoice].Content : "";
                _entryContextEntries.Add(new ContextMenuEntry(
                    _resolvedQuickSelectLabel,
                    detail,
                    Enabled: rememberedChoice >= 0));
            }

            _contextEntryIndex = releasedEntry;
            LockEntry(releasedEntry);
            context.OpenResolved(_entries[releasedEntry].Content, _entryContextEntries, pointer.Position);
            pointer.ConsumeRightGesture();
            return true;
        }

        bool ProcessEntryContextMenu(Pointer pointer)
        {
            ContextMenu? context = _entryContextMenu;
            if (context is null || !context.IsOpen)
                return false;

            context.Update(pointer);
            if (context.WasSelected && _contextEntryIndex >= 0)
            {
                if (context.SelectedIndex < _choices.Length)
                {
                    ChangeEntryChoice(_contextEntryIndex, _choices[context.SelectedIndex].Tag);
                    SelectEntry(_contextEntryIndex);
                }
                else if (context.SelectedIndex == _choices.Length && _resolvedQuickSelectLabel.Length > 0)
                {
                    SelectEntry(_contextEntryIndex);
                }
            }

            if (!context.IsOpen)
            {
                _contextEntryIndex = -1;
                return true;
            }

            // A popup hit keeps priority even when the popup covers another radial wedge. Both ends of the right
            // gesture must be outside the popup before an exposed enabled wedge can become the new target.
            var contextBounds = context.Bounds;
            bool popupOwnsRightGesture = contextBounds.Contains(pointer.RightPressOrigin) ||
                pointer.IsPointerIn(contextBounds);
            if (!popupOwnsRightGesture)
                OpenEntryContextMenu(pointer);
            return true;
        }

        bool CanQuickSelectEntry(int entryIndex)
        {
            if (entryIndex < 0 || !_entries[entryIndex].Enabled)
                return false;
            return _choices.Length == 0 || ChoiceIndexForEntry(entryIndex) >= 0;
        }

        void ClearQuickSelectPreview() => _quickSelectPreviewIndex = -1;

        void UpdateQuickSelectPreview(bool quickSelect) =>
            _quickSelectPreviewIndex = quickSelect && _choices.Length > 0 && HoverIndex >= 0
                ? HoverIndex
                : -1;

        void CloseEntryContextMenu()
        {
            _entryContextMenu?.Close();
            _contextEntryIndex = -1;
        }
    }
}
