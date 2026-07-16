using System.Collections.Generic;
using KhaozEngine.Showcase;
using Xunit;

namespace KhaozEngine.Tests.Showcase
{
    public class ShowcaseMenuTests
    {
        static ShowcaseMenu Menu() => new ShowcaseMenu(new List<string> { "2D", "GUI", "Input" });

        [Fact]
        public void Rooms_AreWhatItWasConstructedWith()
        {
            var m = Menu();
            Assert.Equal(new[] { "2D", "GUI", "Input" }, m.Rooms);
            Assert.Equal(0, m.Selected);
            Assert.Equal("2D", m.Current);
        }

        [Fact]
        public void MoveNext_And_MovePrev_Wrap()
        {
            var m = Menu();
            m.MovePrev();
            Assert.Equal(2, m.Selected);      // wraps from 0 to last
            m.MoveNext();
            Assert.Equal(0, m.Selected);      // wraps from last to 0
            m.MoveNext();
            Assert.Equal(1, m.Selected);
        }

        [Fact]
        public void SelectAt_InRange_Sets_OutOfRange_Ignored()
        {
            var m = Menu();
            m.SelectAt(2);
            Assert.Equal(2, m.Selected);
            m.SelectAt(5);                    // out of range: ignored
            Assert.Equal(2, m.Selected);
            m.SelectAt(-1);                   // out of range: ignored
            Assert.Equal(2, m.Selected);
        }

        [Fact]
        public void Empty_Menu_HasNoCurrent()
        {
            var m = new ShowcaseMenu(new List<string>());
            Assert.Empty(m.Rooms);
            Assert.Equal(-1, m.Selected);
            Assert.Null(m.Current);
        }

        // A 7-item, 2-column grid: rows are (0,1) (2,3) (4,5) (6) - the last row holds a single left cell.
        static ShowcaseMenu Grid() =>
            new ShowcaseMenu(new List<string> { "a", "b", "c", "d", "e", "f", "g" }, columns: 2);

        [Fact]
        public void MoveRight_At_RightEdge_IsNoOp()
        {
            var m = Grid();
            m.SelectAt(1);          // right column, top row
            m.MoveRight();
            Assert.Equal(1, m.Selected);
            m.SelectAt(6);          // lone bottom-left cell: no right neighbour exists
            m.MoveRight();
            Assert.Equal(6, m.Selected);
        }

        [Fact]
        public void MoveRight_And_MoveLeft_StayInTheSameRow()
        {
            var m = Grid();
            m.SelectAt(2);          // left cell of row 1
            m.MoveRight();
            Assert.Equal(3, m.Selected);
            m.MoveLeft();
            Assert.Equal(2, m.Selected);
        }

        [Fact]
        public void MoveLeft_At_LeftEdge_IsNoOp()
        {
            var m = Grid();
            m.SelectAt(4);          // left column
            m.MoveLeft();
            Assert.Equal(4, m.Selected);
        }

        [Fact]
        public void MoveDown_From5_ClampsIntoShortLastRow()
        {
            var m = Grid();
            m.SelectAt(5);          // right cell of row 2; row 3 has no right cell
            m.MoveDown();
            Assert.Equal(6, m.Selected);
        }

        [Fact]
        public void MoveDown_From6_IsNoOp()
        {
            var m = Grid();
            m.SelectAt(6);          // already on the last row
            m.MoveDown();
            Assert.Equal(6, m.Selected);
        }

        [Fact]
        public void MoveUp_From6_GoesTo4()
        {
            var m = Grid();
            m.SelectAt(6);
            m.MoveUp();
            Assert.Equal(4, m.Selected);
        }

        [Fact]
        public void MoveUp_At_TopRow_IsNoOp()
        {
            var m = Grid();
            m.SelectAt(1);
            m.MoveUp();
            Assert.Equal(1, m.Selected);
        }

        [Fact]
        public void Grid_RoundTrip_StaysInRange()
        {
            var m = Grid();
            // Walk every direction a few times over and assert the index is always a valid, in-range tile.
            foreach (int step in new[] { 0, 1, 2, 3, 4, 5, 6, 7, 8 })
            {
                switch (step % 4)
                {
                    case 0: m.MoveDown(); break;
                    case 1: m.MoveRight(); break;
                    case 2: m.MoveUp(); break;
                    default: m.MoveLeft(); break;
                }
                Assert.InRange(m.Selected, 0, m.Rooms.Count - 1);
            }
        }
    }
}
