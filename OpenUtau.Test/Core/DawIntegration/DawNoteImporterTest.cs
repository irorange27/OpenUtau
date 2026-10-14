using System.Collections.Generic;
using System.Linq;
using Xunit;
using OpenUtau.Core.DawIntegration;
using OpenUtau.Core.Ustx;

namespace OpenUtau.Test.Core.DawIntegration {
    /// <summary>
    /// Covers the DAW-note conversion in <see cref="DawNoteImporter"/> (PROTOCOL.md §6.3).
    /// </summary>
    /// <remarks>
    /// The arithmetic is anchored to a known fixture: <see cref="UProject"/> starts at
    /// 120 BPM in 4/4 with a 480-tick beat, so one beat is exactly 500 ms and one tick is
    /// 500/480 ms. Every expected tick below is derived from that, not from running the
    /// code and recording what it printed.
    /// </remarks>
    public class DawNoteImporterTest {
        /// <summary>120 BPM, 4/4, 480 ticks per beat: one beat is 500 ms, one tick is 25/24 ms.</summary>
        static UProject NewProject() {
            var project = new UProject();
            project.timeAxis.BuildSegments(project);
            return project;
        }

        static DawNote Note(int tone, double startMs, double durationMs,
                            int? velocity = null, string? lyric = null) =>
            new DawNote {
                Tone = tone,
                StartMs = startMs,
                DurationMs = durationMs,
                Velocity = velocity,
                Lyric = lyric,
            };

        // ---------------------------------------------------------------- timing

        [Fact]
        public void ConvertsMillisecondsToTicksOnTheProjectAxis() {
            var project = NewProject();
            // 1000 ms is beat 3, i.e. tick 960; 500 ms is one beat, i.e. 480 ticks.
            var notes = DawNoteImporter.Convert(project,
                new[] { Note(60, 1000, 500) }, "la", 0, 60000);

            Assert.Single(notes);
            Assert.Equal(960, notes[0].position);
            Assert.Equal(480, notes[0].duration);
            Assert.Equal(60, notes[0].tone);
        }

        [Fact]
        public void TempoChangeRescalesLaterNotes() {
            var project = NewProject();
            // Double the tempo at tick 0: one beat becomes 250 ms, so 1000 ms is beat 5
            // (tick 1920), not beat 3. This is the project's own axis doing the work.
            project.tempos = new List<UTempo> { new UTempo(0, 240) };
            project.timeAxis.BuildSegments(project);

            var notes = DawNoteImporter.Convert(project,
                new[] { Note(60, 1000, 500) }, "la", 0, 60000);

            Assert.Equal(1920, notes[0].position);
            Assert.Equal(960, notes[0].duration);
        }

        // ---------------------------------------------------------------- window

        [Fact]
        public void DropsNotesOutsideTheRecordedWindow() {
            var project = NewProject();
            var notes = DawNoteImporter.Convert(project, new[] {
                Note(60, 100, 500),    // before the window
                Note(62, 1000, 500),   // inside
                Note(64, 3000, 500),   // at/after the end
            }, "la", 500, 3000);

            Assert.Equal(new[] { 62 }, notes.Select(n => n.tone));
        }

        [Fact]
        public void WindowIsHalfOpenAtTheEnd() {
            var project = NewProject();
            // A note starting exactly at endMs belongs to the next window, not this one.
            var notes = DawNoteImporter.Convert(project,
                new[] { Note(60, 3000, 100) }, "la", 0, 3000);

            Assert.Empty(notes);
        }

        // ---------------------------------------------------------------- degenerate input

        [Fact]
        public void ZeroDurationNoteGetsTheFloor() {
            var project = NewProject();
            var notes = DawNoteImporter.Convert(project,
                new[] { Note(60, 1000, 0) }, "la", 0, 60000);

            Assert.Equal(DawNoteImporter.MinDurationTicks, notes[0].duration);
        }

        [Fact]
        public void NegativeDurationNoteGetsTheFloor() {
            var project = NewProject();
            var notes = DawNoteImporter.Convert(project,
                new[] { Note(60, 1000, -500) }, "la", 0, 60000);

            Assert.Equal(DawNoteImporter.MinDurationTicks, notes[0].duration);
        }

        [Fact]
        public void VeryShortNoteIsRaisedToTheFloorNotDropped() {
            var project = NewProject();
            // One tick's worth: rounds to 1 tick, which the renderer cannot voice.
            var notes = DawNoteImporter.Convert(project,
                new[] { Note(60, 1000, 1) }, "la", 0, 60000);

            Assert.Single(notes);
            Assert.Equal(DawNoteImporter.MinDurationTicks, notes[0].duration);
        }

        [Fact]
        public void OutOfRangeToneIsSkippedWithoutDiscardingTheBatch() {
            var project = NewProject();
            var notes = DawNoteImporter.Convert(project, new[] {
                Note(-1, 1000, 500),
                Note(60, 1000, 500),
                Note(200, 1000, 500),
            }, "la", 0, 60000);

            Assert.Equal(new[] { 60 }, notes.Select(n => n.tone));
        }

        [Fact]
        public void NaNPositionsAreSkipped() {
            var project = NewProject();
            var notes = DawNoteImporter.Convert(project, new[] {
                Note(60, double.NaN, 500),
                Note(62, 1000, double.NaN),
                Note(64, 1000, 500),
            }, "la", 0, 60000);

            Assert.Equal(new[] { 64 }, notes.Select(n => n.tone));
        }

        [Fact]
        public void NegativeStartIsClampedToZero() {
            var project = NewProject();
            // A window can legitimately begin at 0 while a note reports a hair below it.
            var notes = DawNoteImporter.Convert(project,
                new[] { Note(60, -1, 500) }, "la", -10, 60000);

            Assert.Equal(0, notes[0].position);
        }

        // ---------------------------------------------------------------- lyric

        [Fact]
        public void MissingLyricBecomesThePlaceholder() {
            var project = NewProject();
            var notes = DawNoteImporter.Convert(project,
                new[] { Note(60, 1000, 500) }, "la", 0, 60000);

            Assert.Equal("la", notes[0].lyric);
        }

        [Fact]
        public void BlankLyricBecomesThePlaceholder() {
            var project = NewProject();
            var notes = DawNoteImporter.Convert(project,
                new[] { Note(60, 1000, 500, lyric: "   ") }, "la", 0, 60000);

            Assert.Equal("la", notes[0].lyric);
        }

        [Fact]
        public void SuppliedLyricIsKept() {
            var project = NewProject();
            var notes = DawNoteImporter.Convert(project,
                new[] { Note(60, 1000, 500, lyric: "do") }, "la", 0, 60000);

            Assert.Equal("do", notes[0].lyric);
        }

        // ---------------------------------------------------------------- velocity

        [Fact]
        public void AbsentVelocityAddsNoExpression() {
            var project = NewProject();
            var notes = DawNoteImporter.Convert(project,
                new[] { Note(60, 1000, 500) }, "la", 0, 60000);

            Assert.Empty(notes[0].phonemeExpressions);
        }

        [Fact]
        public void MidiCentreVelocityMapsToOpenUtauNeutral() {
            var project = NewProject();
            // MIDI's own centre is 64; OpenUtau's `vel` is neutral at 100.
            var notes = DawNoteImporter.Convert(project,
                new[] { Note(60, 1000, 500, velocity: 64) }, "la", 0, 60000);

            var vel = notes[0].phonemeExpressions.Single();
            Assert.Equal(OpenUtau.Core.Format.Ustx.VEL, vel.abbr);
            Assert.Equal(0, vel.index);
            Assert.Equal(100, vel.value);
        }

        [Fact]
        public void VelocityAboveMidiRangeIsClamped() {
            var project = NewProject();
            var notes = DawNoteImporter.Convert(project,
                new[] { Note(60, 1000, 500, velocity: 200) }, "la", 0, 60000);

            // Clamped to 127, which is 127*100/64 = 198 on OpenUtau's scale.
            Assert.Equal(198, notes[0].phonemeExpressions.Single().value);
        }

        // ---------------------------------------------------------------- ordering

        [Fact]
        public void NotesComeBackSortedByPosition() {
            var project = NewProject();
            var notes = DawNoteImporter.Convert(project, new[] {
                Note(60, 2000, 500),
                Note(62, 1000, 500),
                Note(64, 1500, 500),
            }, "la", 0, 60000);

            Assert.Equal(notes.Select(n => n.position).OrderBy(x => x), notes.Select(n => n.position));
            Assert.Equal(new[] { 62, 64, 60 }, notes.Select(n => n.tone));
        }

        [Fact]
        public void SimultaneousNotesAreAllKept() {
            var project = NewProject();
            // A chord: same start, three voices.
            var notes = DawNoteImporter.Convert(project, new[] {
                Note(60, 1000, 500),
                Note(64, 1000, 500),
                Note(67, 1000, 500),
            }, "la", 0, 60000);

            Assert.Equal(3, notes.Count);
            Assert.Equal(new[] { 60, 64, 67 }, notes.Select(n => n.tone).OrderBy(x => x));
        }

        [Fact]
        public void EmptyBatchProducesNothing() {
            var project = NewProject();
            Assert.Empty(DawNoteImporter.Convert(project, new List<DawNote>(), "la", 0, 60000));
        }

        // ---------------------------------------------------------------- target part

        [Fact]
        public void FindsThePartCoveringTheFirstNote() {
            var project = NewProject();
            var part = new UVoicePart { trackNo = 0, position = 960, duration = 960 };
            project.parts.Add(part);

            var notes = new List<UNote> { new UNote { position = 1000, duration = 100 } };
            Assert.Same(part, DawNoteImporter.FindTargetPart(project, 0, notes));
        }

        [Fact]
        public void FindsNoPartWhenTheNotesFallOutsideEveryPart() {
            var project = NewProject();
            project.parts.Add(new UVoicePart { trackNo = 0, position = 0, duration = 480 });

            var notes = new List<UNote> { new UNote { position = 5000, duration = 100 } };
            Assert.Null(DawNoteImporter.FindTargetPart(project, 0, notes));
        }

        [Fact]
        public void IgnoresPartsOnOtherTracks() {
            var project = NewProject();
            project.tracks.Add(new UTrack("Track2"));
            project.parts.Add(new UVoicePart { trackNo = 1, position = 0, duration = 9600 });

            var notes = new List<UNote> { new UNote { position = 100, duration = 100 } };
            Assert.Null(DawNoteImporter.FindTargetPart(project, 0, notes));
        }

        [Fact]
        public void OutOfRangeTrackHasNoPart() {
            var project = NewProject();
            var notes = new List<UNote> { new UNote { position = 0, duration = 100 } };
            Assert.Null(DawNoteImporter.FindTargetPart(project, 99, notes));
        }
    }
}
