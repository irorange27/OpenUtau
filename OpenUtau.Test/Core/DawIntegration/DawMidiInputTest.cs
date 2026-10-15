using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using OpenUtau.Core.Ustx;
using Xunit;

namespace OpenUtau.Core.DawIntegration {
    /// <summary>
    /// The MIDI-input flow of §6.3 end to end: a real plugin-side TCP server drives
    /// <c>recordStart → midiNotes → recordStop</c> against the shipping
    /// <see cref="DawManager"/>, over loopback, and the notes land in the project.
    /// </summary>
    [Collection(DawIntegrationCollection.Name)]
    public class DawMidiInputTest : IDisposable {
        private readonly string directory;
        private readonly DawTestPlugin plugin;
        private readonly UProject project;
        private DawManager? manager;
        private readonly UProject previousProject;

        public DawMidiInputTest() {
            directory = Path.Combine(Path.GetTempPath(), "OpenUtauDawMidiTest", Guid.NewGuid().ToString("N"));
            plugin = DawTestPlugin.Start("MidiInputPlugin", directory);
            project = new UProject();
            // v1.1 refuses to sync an unsaved project (the USTX serializer needs a FilePath for
            // relative paths), and the connect handshake sends init before anything else.
            project.FilePath = Path.Combine(Path.GetTempPath(), "daw-midi-input-test.ustx");
            project.timeAxis.BuildSegments(project);
            // Applying notes goes through DocManager.ExecuteCmd, which otherwise posts to a UI
            // thread that does not exist here. The sink seam runs the command inline, which is
            // the same thing the UI thread would do.
            previousProject = DocManager.Inst.TakeProjectForTest(project);
            DocManager.Inst.CommandSink = cmd => cmd.Execute();
        }

        public void Dispose() {
            DocManager.Inst.CommandSink = null!;
            DocManager.Inst.TakeProjectForTest(previousProject);
            manager?.Dispose();
            plugin.Dispose();
            try {
                if (Directory.Exists(directory)) {
                    Directory.Delete(directory, recursive: true);
                }
            } catch (Exception) {
                // A leftover temp directory is not worth failing a test over.
            }
        }

        private async Task<DawManager> ConnectAsync() {
            var created = new DawManager(null, null, new[] {
                TimeSpan.FromMilliseconds(20),
                TimeSpan.FromMilliseconds(20),
                TimeSpan.FromMilliseconds(20),
            }) {
                UseTimerPump = false,
                ProjectSource = () => project,
            };
            manager = created;
            await created.ConnectAsync(plugin.Advertisement);
            Assert.Equal(DawConnectionState.Connected, created.State);
            return created;
        }

        private static DawNote Note(int tone, double startMs, double durationMs, int? velocity = null) =>
            new DawNote { Tone = tone, StartMs = startMs, DurationMs = durationMs, Velocity = velocity };

        /// <summary>Waits until the project holds at least this many notes, or fails on timeout.</summary>
        private async Task WaitForNotesAsync(int expected) {
            for (int i = 0; i < 200; i++) {
                int count = project.parts.OfType<UVoicePart>().Sum(p => p.notes.Count);
                if (count >= expected) {
                    return;
                }
                await Task.Delay(25);
            }
            int actual = project.parts.OfType<UVoicePart>().Sum(p => p.notes.Count);
            Assert.Fail($"Expected {expected} notes in the project, found {actual}.");
        }

        // ---------------------------------------------------------------- the happy path

        [Fact]
        public async Task RecordedNotesLandInTheProject() {
            var openutau = await ConnectAsync();

            await openutau.StartMidiRecordingAsync(0, 0, 60000);
            Assert.True(openutau.IsMidiRecording);
            // §6.3: the plugin is told the track and the window.
            Assert.Equal(0, plugin.RecordTrackNo);
            Assert.Equal((0d, 60000d), plugin.RecordWindow);

            await plugin.SendMidiNotesAsync(new[] {
                Note(60, 1000, 500),
                Note(62, 1500, 500),
            });
            int applied = await openutau.StopMidiRecordingAsync();

            Assert.Equal(2, applied);
            Assert.False(openutau.IsMidiRecording);
            await WaitForNotesAsync(2);

            var part = project.parts.OfType<UVoicePart>().Single();
            Assert.Equal(0, part.trackNo);
            var notes = part.notes.OrderBy(n => n.position).ToList();
            // 1000 ms is beat 3 (tick 960) at 120 BPM in 4/4.
            Assert.Equal(960, notes[0].position);
            Assert.Equal(60, notes[0].tone);
            Assert.Equal(1440, notes[1].position);
            Assert.Equal(62, notes[1].tone);
            // MIDI carries no lyric, so the placeholder stands (§6.3.2).
            Assert.All(notes, n => Assert.Equal(DawManager.DefaultDawLyric, n.lyric));
        }

        [Fact]
        public async Task NoPartCoveringTheWindowGetsOneCreated() {
            var openutau = await ConnectAsync();
            Assert.Empty(project.parts);

            await openutau.StartMidiRecordingAsync(0, 0, 60000);
            await plugin.SendMidiNotesAsync(new[] { Note(60, 1000, 500) });
            await openutau.StopMidiRecordingAsync();

            await WaitForNotesAsync(1);
            var part = Assert.Single(project.parts.OfType<UVoicePart>());
            // The new part starts at the first note, not at zero, so an empty track gains
            // exactly the recorded material.
            Assert.Equal(960, part.position);
            Assert.Equal(DawManager.DefaultDawLyric, part.notes.Single().lyric);
        }

        [Fact]
        public async Task NotesGoIntoAnExistingPartRatherThanANewOne() {
            var existing = new UVoicePart { trackNo = 0, position = 0, duration = 9600, name = "Mine" };
            project.parts.Add(existing);
            var openutau = await ConnectAsync();

            await openutau.StartMidiRecordingAsync(0, 0, 60000);
            await plugin.SendMidiNotesAsync(new[] { Note(60, 1000, 500) });
            await openutau.StopMidiRecordingAsync();

            await WaitForNotesAsync(1);
            Assert.Same(existing, Assert.Single(project.parts.OfType<UVoicePart>()));
            Assert.Single(existing.notes);
        }

        // ---------------------------------------------------------------- session discipline

        [Fact]
        public async Task NotesForAnUnknownSessionAreDroppedNotFatal() {
            var openutau = await ConnectAsync();
            // No session open: the batch must be ignored, and the connection must survive it.
            await plugin.SendMidiNotesForSessionAsync("r-nope", new[] { Note(60, 1000, 500) });
            await Task.Delay(100);

            Assert.Equal(DawConnectionState.Connected, openutau.State);
            Assert.Empty(project.parts);
        }

        [Fact]
        public async Task ASessionThatIsNeverOpenedProducesNothing() {
            var openutau = await ConnectAsync();
            // Stopping without starting is not an error; it simply applies nothing.
            Assert.Equal(0, await openutau.StopMidiRecordingAsync());
            Assert.Empty(project.parts);
        }

        [Fact]
        public async Task SecondStartWhileOneIsOpenIsRefused() {
            var openutau = await ConnectAsync();
            await openutau.StartMidiRecordingAsync(0, 0, 60000);

            var error = await Assert.ThrowsAsync<InvalidOperationException>(
                () => openutau.StartMidiRecordingAsync(0, 0, 60000));
            Assert.Equal("session_already_open", error.Message);
        }

        [Fact]
        public async Task CancellingDiscardsTheRecordedNotes() {
            var openutau = await ConnectAsync();
            await openutau.StartMidiRecordingAsync(0, 0, 60000);
            await plugin.SendMidiNotesAsync(new[] { Note(60, 1000, 500) });

            int applied = await openutau.StopMidiRecordingAsync(commit: false);

            Assert.Equal(0, applied);
            // §6.3: the plugin is still told, so it stops recording either way.
            Assert.False(plugin.SessionOpen);
            Assert.False(plugin.LastStopCommitted);
            Assert.Empty(project.parts);
        }

        [Fact]
        public async Task NotesOutsideTheRecordedWindowAreDropped() {
            var openutau = await ConnectAsync();
            await openutau.StartMidiRecordingAsync(0, 1000, 3000);

            await plugin.SendMidiNotesAsync(new[] {
                Note(60, 100, 500),    // before the window
                Note(62, 1500, 500),   // inside
                Note(64, 5000, 500),   // after it
            });
            int applied = await openutau.StopMidiRecordingAsync();

            Assert.Equal(1, applied);
            await WaitForNotesAsync(1);
            Assert.Equal(62, project.parts.OfType<UVoicePart>().Single().notes.Single().tone);
        }

        // ---------------------------------------------------------------- batching

        [Fact]
        public async Task SeveralBatchesAccumulateIntoOneSession() {
            var openutau = await ConnectAsync();
            await openutau.StartMidiRecordingAsync(0, 0, 60000);

            // §6.3: the sender chooses the pacing; the receiver must tolerate any batching.
            await plugin.SendMidiNotesAsync(new[] { Note(60, 1000, 500) });
            await plugin.SendMidiNotesAsync(new[] { Note(62, 1500, 500), Note(64, 2000, 500) });
            await plugin.SendMidiNotesAsync(new[] { Note(65, 2500, 500) });
            int applied = await openutau.StopMidiRecordingAsync();

            Assert.Equal(4, applied);
            await WaitForNotesAsync(4);
        }

        [Fact]
        public async Task AChordInOneBatchIsKeptWhole() {
            var openutau = await ConnectAsync();
            await openutau.StartMidiRecordingAsync(0, 0, 60000);
            await plugin.SendMidiNotesAsync(new[] {
                Note(60, 1000, 500),
                Note(64, 1000, 500),
                Note(67, 1000, 500),
            });
            int applied = await openutau.StopMidiRecordingAsync();

            Assert.Equal(3, applied);
            await WaitForNotesAsync(3);
            var notes = project.parts.OfType<UVoicePart>().Single().notes;
            Assert.Equal(new[] { 60, 64, 67 }, notes.Select(n => n.tone).OrderBy(t => t));
        }

        // ---------------------------------------------------------------- interaction with the rest

        [Fact]
        public async Task RecordingDoesNotDisturbTheExistingSyncFlow() {
            var openutau = await ConnectAsync();
            await openutau.StartMidiRecordingAsync(0, 0, 60000);
            await plugin.SendMidiNotesAsync(new[] { Note(60, 1000, 500) });
            await openutau.StopMidiRecordingAsync();

            // The handshake messages still went out in §7 order, with the record messages
            // interleaved where they belong.
            var received = plugin.Received.ToArray();
            Assert.Equal(DawMessageKind.Init, received[0]);
            Assert.Contains(DawMessageKind.RecordStart, received);
            Assert.Contains(DawMessageKind.RecordStop, received);
            Assert.Equal(DawConnectionState.Connected, openutau.State);
        }
    }
}
