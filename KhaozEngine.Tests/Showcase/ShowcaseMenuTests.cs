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
    }
}
