using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.App;
using KhaozEngine.Primitives;
using KhaozEngine.Windowing;

namespace KhaozEngine.Gui
{
    public sealed partial class RadialMenu
    {
        string _title = "";
        ResolvedRadialMenuEntry[] _entries = [];
        ResolvedRadialMenuChoice[] _choices = [];
        long[] _entryChoiceTags = [];
        Vector2 _requestedAnchor;
        Vector2 _center;
        int _focusedEntryIndex = -1;
        int _pointerEntryIndex = -1;
        int _focusedChoiceIndex = -1;
        bool _footerFocused;
        bool _openedThisFrame;
        bool _openingGestureLatch;
        float _sheenPhase;
        Pointer? _blockingPointer;
        RadialMenuMetrics _metrics = RadialMenuMetrics.Default;
        Rect _safeBounds;

        public RadialMenuMetrics Metrics
        {
            get => _metrics;
            set
            {
                ValidateGeometry(value);
                if (IsOpen)
                {
                    (Vector2 center, Rect bounds) = ComputeLayout(_requestedAnchor, _safeBounds, value);
                    _metrics = value;
                    ApplyLayout(center, bounds);
                    return;
                }
                _metrics = value;
            }
        }

        public Rect SafeBounds
        {
            get => _safeBounds;
            set
            {
                ValidateRectangle(value, nameof(SafeBounds));
                if (IsOpen)
                {
                    (Vector2 center, Rect bounds) = ComputeLayout(_requestedAnchor, value, _metrics);
                    _safeBounds = value;
                    ApplyLayout(center, bounds);
                    return;
                }
                _safeBounds = value;
            }
        }
        public bool IsOpen { get; private set; }
        public int HoverIndex { get; private set; } = -1;
        public int ActiveIndex { get; private set; } = -1;
        public bool WasSelected { get; private set; }
        public RadialMenuSelection Selection { get; private set; }
        public bool WasChoiceChanged { get; private set; }
        public RadialMenuChoiceChange ChoiceChange { get; private set; }
        public bool WasDismissed { get; private set; }
        public Rect Bounds { get; private set; }

        internal string Title => _title;
        internal IReadOnlyList<ResolvedRadialMenuEntry> Entries => _entries;
        internal IReadOnlyList<ResolvedRadialMenuChoice> Choices => _choices;
        internal Vector2 Center => _center;
        internal int FocusedChoiceIndex => _focusedChoiceIndex;
        internal bool FooterFocused => _footerFocused;
        internal float SheenPhase => _sheenPhase;

        public void Open(
            LocalizedText title,
            IReadOnlyList<RadialMenuEntry> entries,
            Vector2 anchor,
            IReadOnlyList<RadialMenuChoice>? choices = null)
        {
            ArgumentNullException.ThrowIfNull(entries);
            ValidateEntryCount(entries.Count);

            int choiceCount = choices?.Count ?? 0;
            ValidateChoiceCount(choiceCount);
            ValidateUniqueEntryTags(entries);
            if (choices != null) ValidateUniqueChoiceTags(choices);

            Vector2 center = ComputeCenter(anchor, SafeBounds, entries.Count, choiceCount, Metrics);
            Rect bounds = ComputeBounds(center, entries.Count, choiceCount, Metrics);

            var resolvedEntries = new ResolvedRadialMenuEntry[entries.Count];
            var resolvedChoices = new ResolvedRadialMenuChoice[choiceCount];
            var entryChoiceTags = new long[entries.Count];

            for (int i = 0; i < choiceCount; i++)
            {
                RadialMenuChoice choice = choices![i];
                resolvedChoices[i] = new ResolvedRadialMenuChoice(
                    choice.Content.Resolve() ?? "",
                    choice.Tag,
                    choice.Enabled);
            }

            for (int i = 0; i < entries.Count; i++)
            {
                RadialMenuEntry entry = entries[i];
                resolvedEntries[i] = new ResolvedRadialMenuEntry(
                    entry.Content.Resolve() ?? "",
                    entry.Tag,
                    entry.IconId,
                    entry.Enabled,
                    entry.Detail.Resolve() ?? "");
                entryChoiceTags[i] = ResolveInitialChoice(entry.InitialChoiceTag, resolvedChoices);
            }

            _title = title.Resolve() ?? "";
            _entries = resolvedEntries;
            _choices = resolvedChoices;
            _entryChoiceTags = entryChoiceTags;
            _requestedAnchor = anchor;
            _center = center;
            Bounds = bounds;
            _focusedEntryIndex = FindFirstEnabledEntry();
            _pointerEntryIndex = -1;
            ActiveIndex = _focusedEntryIndex;
            _focusedChoiceIndex = ChoiceIndexForEntry(_focusedEntryIndex);
            _footerFocused = false;
            _openedThisFrame = true;
            _openingGestureLatch = true;
            _sheenPhase = 0f;
            _blockingPointer = null;
            IsOpen = true;
            HoverIndex = -1;
            ClearFrameFlags();
        }

        (Vector2 Center, Rect Bounds) ComputeLayout(
            Vector2 anchor,
            Rect safeBounds,
            RadialMenuMetrics metrics)
        {
            Vector2 center = ComputeCenter(anchor, safeBounds, _entries.Length, _choices.Length, metrics);
            Rect bounds = ComputeBounds(center, _entries.Length, _choices.Length, metrics);
            return (center, bounds);
        }

        void ApplyLayout(Vector2 center, Rect bounds)
        {
            _center = center;
            Bounds = bounds;
            InvalidateDrawLayoutCache();
            _blockingPointer?.BlockRegion(bounds);
        }

        public bool SetEntryChoice(long entryTag, long choiceTag)
        {
            int entryIndex = EntryIndexForTag(entryTag);
            int choiceIndex = ChoiceIndexForTag(choiceTag);
            if (entryIndex < 0 || choiceIndex < 0 || !_choices[choiceIndex].Enabled)
                return false;
            if (_entryChoiceTags[entryIndex] == choiceTag)
                return false;

            _entryChoiceTags[entryIndex] = choiceTag;
            if (_focusedEntryIndex == entryIndex)
                _focusedChoiceIndex = choiceIndex;
            return true;
        }

        public bool Update(Pointer pointer, float dt)
        {
            ArgumentNullException.ThrowIfNull(pointer);
            ClearFrameFlags();
            if (!IsOpen)
            {
                _blockingPointer = null;
                HoverIndex = -1;
                _openedThisFrame = false;
                _openingGestureLatch = false;
                return false;
            }

            _sheenPhase = NormalizePhase(_sheenPhase + MathF.Max(0f, dt) * Metrics.SheenSpeed);

            bool openingFrame = _openedThisFrame;
            _openedThisFrame = false;
            if (_openingGestureLatch && !openingFrame && pointer.IsPressOriginFresh)
                _openingGestureLatch = false;

            _blockingPointer = pointer;
            pointer.BlockRegion(Bounds);

            HoverIndex = EntryAt(pointer.Position, _center, _entries.Length, Metrics);
            if (HoverIndex >= 0)
            {
                _pointerEntryIndex = HoverIndex;
                ActiveIndex = HoverIndex;
            }
            else if (!pointer.IsPointerIn(Bounds))
            {
                _pointerEntryIndex = -1;
                ActiveIndex = _focusedEntryIndex;
            }

            if (_openingGestureLatch)
                return false;

            if (ProcessFooterTap(pointer))
                return false;

            if (ProcessWedgeTap(pointer))
                return true;

            if (pointer.IsReleasedOutside(Bounds))
            {
                WasDismissed = true;
                pointer.ConsumeGesture();
                Close();
            }

            return false;
        }

        public bool Update(InputManager input, float dt, bool focused, PlayerIndex? player = null)
        {
            ArgumentNullException.ThrowIfNull(input);
            bool selected = Update(input.Pointer, dt);
            if (!IsOpen || !focused || !input.State.WindowFocused)
                return selected;

            if (input.IsMenuCancel(player, out _))
            {
                WasDismissed = true;
                Close();
                return false;
            }

            if (input.IsMenuUp(player))
            {
                _footerFocused = false;
                return false;
            }

            if (input.IsMenuDown(player))
            {
                if (_choices.Length > 0 && ActiveIndex >= 0)
                {
                    _footerFocused = true;
                    _focusedChoiceIndex = ChoiceIndexForEntry(_focusedEntryIndex);
                }
                return false;
            }

            if (input.IsSelectPrevious(player))
            {
                if (_footerFocused)
                    _focusedChoiceIndex = FindEnabledChoice(_focusedChoiceIndex, -1);
                else
                    SetFocusedEntry(FindEnabledEntry(_focusedEntryIndex, -1));
                return false;
            }

            if (input.IsSelectNext(player))
            {
                if (_footerFocused)
                    _focusedChoiceIndex = FindEnabledChoice(_focusedChoiceIndex, 1);
                else
                    SetFocusedEntry(FindEnabledEntry(_focusedEntryIndex, 1));
                return false;
            }

            if (!input.IsMenuSelect(player, out _))
                return false;

            if (_footerFocused)
            {
                CommitFocusedChoice();
                return false;
            }

            if (_focusedEntryIndex >= 0 && _entries[_focusedEntryIndex].Enabled)
            {
                SelectEntry(_focusedEntryIndex);
                return true;
            }

            return false;
        }

        public void Close()
        {
            IsOpen = false;
            HoverIndex = -1;
            ActiveIndex = -1;
            _focusedEntryIndex = -1;
            _pointerEntryIndex = -1;
            _focusedChoiceIndex = -1;
            _footerFocused = false;
            _blockingPointer = null;
        }

        void ClearFrameFlags()
        {
            WasSelected = false;
            Selection = default;
            WasChoiceChanged = false;
            ChoiceChange = default;
            WasDismissed = false;
        }

        bool ProcessFooterTap(Pointer pointer)
        {
            int entryIndex = _pointerEntryIndex >= 0 ? _pointerEntryIndex : ActiveIndex;
            if (entryIndex < 0)
                return false;

            for (int i = 0; i < _choices.Length; i++)
            {
                if (!_choices[i].Enabled)
                    continue;

                Rect bounds = ChoiceBounds(_center, _choices.Length, i, Metrics);
                if (!pointer.IsTapIn(bounds))
                    continue;

                ChangeEntryChoice(entryIndex, _choices[i].Tag);
                return true;
            }

            return false;
        }

        bool ProcessWedgeTap(Pointer pointer)
        {
            if (!pointer.IsTapIn(Bounds))
                return false;

            int pressedEntry = EntryAt(pointer.PressOrigin, _center, _entries.Length, Metrics);
            int releasedEntry = EntryAt(pointer.Position, _center, _entries.Length, Metrics);
            if (pressedEntry < 0 || pressedEntry != releasedEntry)
                return false;

            if (!_entries[releasedEntry].Enabled)
                return false;

            SelectEntry(releasedEntry);
            return true;
        }

        void SelectEntry(int entryIndex)
        {
            ResolvedRadialMenuEntry entry = _entries[entryIndex];
            WasSelected = true;
            Selection = new RadialMenuSelection(entry.Tag, _entryChoiceTags[entryIndex]);
            Close();
        }

        void CommitFocusedChoice()
        {
            if (_focusedEntryIndex < 0 || _focusedChoiceIndex < 0 || !_choices[_focusedChoiceIndex].Enabled)
                return;
            ChangeEntryChoice(_focusedEntryIndex, _choices[_focusedChoiceIndex].Tag);
        }

        void ChangeEntryChoice(int entryIndex, long choiceTag)
        {
            if (_entryChoiceTags[entryIndex] == choiceTag)
                return;

            _entryChoiceTags[entryIndex] = choiceTag;
            WasChoiceChanged = true;
            ChoiceChange = new RadialMenuChoiceChange(_entries[entryIndex].Tag, choiceTag);
        }

        void SetFocusedEntry(int entryIndex)
        {
            if (entryIndex < 0 || entryIndex == _focusedEntryIndex)
                return;
            _focusedEntryIndex = entryIndex;
            _focusedChoiceIndex = ChoiceIndexForEntry(entryIndex);
            if (_pointerEntryIndex < 0)
                ActiveIndex = entryIndex;
        }

        int FindFirstEnabledEntry()
        {
            for (int i = 0; i < _entries.Length; i++)
                if (_entries[i].Enabled)
                    return i;
            return 0;
        }

        int FindEnabledEntry(int current, int direction)
        {
            if (_entries.Length == 0)
                return -1;
            for (int offset = 1; offset <= _entries.Length; offset++)
            {
                int index = WrapIndex(current + direction * offset, _entries.Length);
                if (_entries[index].Enabled)
                    return index;
            }
            return current;
        }

        int FindEnabledChoice(int current, int direction)
        {
            if (_choices.Length == 0)
                return -1;
            for (int offset = 1; offset <= _choices.Length; offset++)
            {
                int index = WrapIndex(current + direction * offset, _choices.Length);
                if (_choices[index].Enabled)
                    return index;
            }
            return current;
        }

        int ChoiceIndexForEntry(int entryIndex)
        {
            if (entryIndex < 0)
                return -1;
            int choiceIndex = ChoiceIndexForTag(_entryChoiceTags[entryIndex]);
            return choiceIndex >= 0 && _choices[choiceIndex].Enabled ? choiceIndex : -1;
        }

        int EntryIndexForTag(long entryTag)
        {
            for (int i = 0; i < _entries.Length; i++)
                if (_entries[i].Tag == entryTag)
                    return i;
            return -1;
        }

        int ChoiceIndexForTag(long choiceTag)
        {
            for (int i = 0; i < _choices.Length; i++)
                if (_choices[i].Tag == choiceTag)
                    return i;
            return -1;
        }

        static long ResolveInitialChoice(long requestedTag, ResolvedRadialMenuChoice[] choices)
        {
            if (choices.Length == 0)
            {
                if (requestedTag != 0)
                    throw new ArgumentException("An entry cannot name an initial choice when the menu has no choices.");
                return 0;
            }

            for (int i = 0; i < choices.Length; i++)
            {
                if (choices[i].Tag != requestedTag)
                    continue;
                if (!choices[i].Enabled && requestedTag != 0)
                    throw new ArgumentException("An entry initial choice must be enabled.");
                if (choices[i].Enabled)
                    return requestedTag;
            }

            if (requestedTag == 0)
            {
                for (int i = 0; i < choices.Length; i++)
                    if (choices[i].Enabled)
                        return choices[i].Tag;
                return 0;
            }

            throw new ArgumentException("An entry initial choice must identify an enabled menu choice.");
        }

        static void ValidateUniqueEntryTags(IReadOnlyList<RadialMenuEntry> entries)
        {
            for (int i = 0; i < entries.Count; i++)
                for (int j = i + 1; j < entries.Count; j++)
                    if (entries[i].Tag == entries[j].Tag)
                        throw new ArgumentException("Radial menu entry tags must be unique.", nameof(entries));
        }

        static void ValidateUniqueChoiceTags(IReadOnlyList<RadialMenuChoice> choices)
        {
            for (int i = 0; i < choices.Count; i++)
                for (int j = i + 1; j < choices.Count; j++)
                    if (choices[i].Tag == choices[j].Tag)
                        throw new ArgumentException("Radial menu choice tags must be unique.", nameof(choices));
        }

        static int WrapIndex(int value, int count)
        {
            int wrapped = value % count;
            return wrapped < 0 ? wrapped + count : wrapped;
        }

        static float NormalizePhase(float value)
        {
            float normalized = value % 1f;
            return normalized < 0f ? normalized + 1f : normalized;
        }
    }
}
