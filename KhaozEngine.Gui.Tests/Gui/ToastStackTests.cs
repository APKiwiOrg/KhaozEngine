using System.Collections.Generic;
using KhaozEngine.App;
using KhaozEngine.Gui;
using Xunit;

namespace KhaozEngine.Tests.Gui
{
    /// <summary>
    /// Headless coverage of <see cref="ToastStack"/>: newest-first insertion, sticky vs timed expiry, the
    /// MaxVisible eviction policy (oldest non-sticky first), keyed in-place replacement, and the dismiss/clear
    /// surface. No rendering or input involved, only the retained model driven by <see cref="ToastStack.Update"/>.
    /// </summary>
    public class ToastStackTests
    {
        static int IndexOfToast(IReadOnlyList<Toast> active, Toast toast)
        {
            for (int i = 0; i < active.Count; i++)
                if (ReferenceEquals(active[i], toast)) return i;
            return -1;
        }

        [Fact]
        public void Show_inserts_newest_first()
        {
            var stack = new ToastStack();

            stack.Show(LocalizedText.Raw("first"));
            stack.Show(LocalizedText.Raw("second"));

            Assert.Equal(2, stack.Active.Count);
            Assert.Equal("second", stack.Active[0].Message.Resolve());
            Assert.Equal("first", stack.Active[1].Message.Resolve());
        }

        [Fact]
        public void Show_with_null_duration_uses_DefaultDuration()
        {
            var stack = new ToastStack();

            Toast t = stack.Show(LocalizedText.Raw("hello"));

            Assert.Equal(6f, stack.DefaultDuration);
            Assert.Equal(6f, t.Duration);

            stack.DefaultDuration = 3f;
            Toast t2 = stack.Show(LocalizedText.Raw("hello again"));
            Assert.Equal(3f, t2.Duration);
        }

        [Fact]
        public void Update_counts_down_and_removes_expired()
        {
            var stack = new ToastStack();
            Toast t = stack.Show(LocalizedText.Raw("timed"), duration: 2f);

            stack.Update(1f);
            Assert.Single(stack.Active);
            Assert.Equal(1f, t.Remaining);

            stack.Update(1.5f);
            Assert.Empty(stack.Active);
        }

        [Fact]
        public void Sticky_toasts_never_expire()
        {
            var stack = new ToastStack();
            Toast sticky = stack.ShowSticky(LocalizedText.Raw("sticky"));
            Toast zeroDuration = stack.Show(LocalizedText.Raw("zero"), duration: 0f);
            Toast negativeDuration = stack.Show(LocalizedText.Raw("negative"), duration: -1f);

            Assert.True(sticky.IsSticky);
            Assert.True(zeroDuration.IsSticky);
            Assert.True(negativeDuration.IsSticky);

            stack.Update(99999f);

            Assert.Equal(3, stack.Active.Count);
        }

        [Fact]
        public void MaxVisible_cap_evicts_oldest_non_sticky_toast()
        {
            var stack = new ToastStack();
            for (int i = 0; i < 5; i++) stack.Show(LocalizedText.Raw($"toast{i}"));
            Toast oldest = stack.Active[^1];
            Assert.Equal("toast0", oldest.Message.Resolve());

            stack.Show(LocalizedText.Raw("toast5"));

            Assert.Equal(5, stack.Active.Count);
            Assert.DoesNotContain(oldest, stack.Active);
            Assert.Equal("toast5", stack.Active[0].Message.Resolve());
        }

        [Fact]
        public void Sticky_toast_survives_the_cap_preferentially()
        {
            var stack = new ToastStack { MaxVisible = 5 };
            Toast sticky = stack.ShowSticky(LocalizedText.Raw("sticky"));
            Toast oldestTimed = stack.Show(LocalizedText.Raw("timed0"));
            stack.Show(LocalizedText.Raw("timed1"));
            stack.Show(LocalizedText.Raw("timed2"));
            stack.Show(LocalizedText.Raw("timed3"));

            stack.Show(LocalizedText.Raw("timed4"));

            Assert.Equal(5, stack.Active.Count);
            Assert.Contains(sticky, stack.Active);
            Assert.DoesNotContain(oldestTimed, stack.Active);
        }

        [Fact]
        public void All_sticky_over_cap_evicts_the_oldest_sticky()
        {
            var stack = new ToastStack { MaxVisible = 2 };
            Toast first = stack.ShowSticky(LocalizedText.Raw("s0"));
            stack.ShowSticky(LocalizedText.Raw("s1"));
            stack.ShowSticky(LocalizedText.Raw("s2"));

            Assert.Equal(2, stack.Active.Count);
            Assert.DoesNotContain(first, stack.Active);
        }

        [Fact]
        public void Keyed_show_replaces_in_place_at_the_original_index()
        {
            var stack = new ToastStack();
            Toast a = stack.Show(LocalizedText.Raw("A"), key: "k");
            stack.Show(LocalizedText.Raw("B"));
            int indexOfA = IndexOfToast(stack.Active, a);
            Assert.NotEqual(0, indexOfA);

            Toast c = stack.Show(LocalizedText.Raw("C"), key: "k");

            Assert.Equal(2, stack.Active.Count);
            Assert.Equal(indexOfA, IndexOfToast(stack.Active, c));
            Assert.DoesNotContain(a, stack.Active);
            Assert.Equal("C", stack.Active[indexOfA].Message.Resolve());
        }

        [Fact]
        public void Keyed_replacement_can_flip_stickiness_both_ways()
        {
            var stack = new ToastStack();
            stack.ShowSticky(LocalizedText.Raw("down"), key: "server");
            Toast backOnline = stack.Show(LocalizedText.Raw("back online"), duration: 4f, key: "server");

            Assert.False(backOnline.IsSticky);
            Assert.Single(stack.Active);

            Toast downAgain = stack.ShowSticky(LocalizedText.Raw("down again"), key: "server");

            Assert.True(downAgain.IsSticky);
            Assert.Single(stack.Active);
        }

        [Fact]
        public void Null_key_shows_never_replace_each_other()
        {
            var stack = new ToastStack();
            stack.Show(LocalizedText.Raw("one"));
            stack.Show(LocalizedText.Raw("two"));

            Assert.Equal(2, stack.Active.Count);
        }

        [Fact]
        public void Clear_removes_by_key_ordinally_and_reports_found()
        {
            var stack = new ToastStack();
            stack.Show(LocalizedText.Raw("keyed"), key: "k");

            Assert.False(stack.Clear("missing"));
            Assert.False(stack.Clear("K")); // ordinal: case differs, no match
            Assert.True(stack.Clear("k"));
            Assert.Empty(stack.Active);
            Assert.False(stack.Clear("k"));
        }

        [Fact]
        public void Dismiss_by_instance_and_by_index()
        {
            var stack = new ToastStack();
            Toast a = stack.Show(LocalizedText.Raw("A"));
            stack.Show(LocalizedText.Raw("B"));

            Assert.False(stack.Dismiss(new Toast()));
            Assert.True(stack.Dismiss(a));
            Assert.DoesNotContain(a, stack.Active);

            Assert.False(stack.Dismiss(5));
            Assert.False(stack.Dismiss(-1));
            Assert.True(stack.Dismiss(0));
            Assert.Empty(stack.Active);
        }

        [Fact]
        public void ClearAll_empties_the_stack()
        {
            var stack = new ToastStack();
            stack.Show(LocalizedText.Raw("A"));
            stack.ShowSticky(LocalizedText.Raw("B"));

            stack.ClearAll();

            Assert.Empty(stack.Active);
        }

        [Fact]
        public void Lowering_MaxVisible_trims_on_the_next_Update()
        {
            var stack = new ToastStack();
            for (int i = 0; i < 5; i++) stack.Show(LocalizedText.Raw($"toast{i}"));

            stack.MaxVisible = 2;
            Assert.Equal(5, stack.Active.Count);

            stack.Update(0f);

            Assert.Equal(2, stack.Active.Count);
        }

        [Fact]
        public void Update_on_an_empty_stack_does_not_throw()
        {
            var stack = new ToastStack();

            stack.Update(1f);

            Assert.Empty(stack.Active);
        }
    }
}
