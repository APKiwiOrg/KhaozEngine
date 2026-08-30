using KhaozEngine.Gui;
using KhaozEngine.Primitives;
using Xunit;

namespace KhaozEngine.Tests.Gui
{
    /// <summary>
    /// The cooldown fan is built per frame per slot on two immediate-mode HUD paths (SlotGrid.Draw and
    /// GuiSurface.CooldownOverlay), so an ability bar with several slots on cooldown used to churn a
    /// List&lt;CooldownQuad&gt;, a float[4] and a List&lt;float&gt; per slot per frame (#108). The quads are consumed
    /// immediately and thrown away, so the geometry belongs on the stack.
    /// </summary>
    [Collection("AllocSensitive")]
    public sealed class CooldownSweepAllocationTests
    {
        static readonly Rect Slot = new(0, 0, 64, 64);

        [Fact]
        public void BuildingTheFanAllocatesNothing()
        {
            // Every fraction band the sweep can be in: no corners inside the arc, some, and all four.
            float[] fractions = { 0.05f, 0.25f, 0.5f, 0.75f, 1f };
            var buffer = new GuiDraw.CooldownQuad[GuiDraw.MaxCooldownQuads];
            Warm(fractions, buffer);

            AllocAssert.NoPerCallAllocation("GuiDraw.CooldownSweepQuads", () =>
            {
                for (int frame = 0; frame < 200; frame++)
                    foreach (float f in fractions)
                        GuiDraw.CooldownSweepQuads(Slot, f, buffer);
            });
        }

        // First call JITs the method and touches its statics. Measuring that would attribute one-off runtime
        // bytes to the per-call cost this test is about.
        static void Warm(float[] fractions, GuiDraw.CooldownQuad[] buffer)
        {
            foreach (float f in fractions) GuiDraw.CooldownSweepQuads(Slot, f, buffer);
        }
    }
}
