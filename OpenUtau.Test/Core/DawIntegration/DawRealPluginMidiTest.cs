using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using OpenUtau.Core.Ustx;
using Xunit;

namespace OpenUtau.Core.DawIntegration {
    /// <summary>
    /// The MIDI-input flow (§6.3) against the real C++ plugin, not a stand-in.
    ///
    /// This is the test that matters for this feature. The C++ suite drives its session against a
    /// fake OpenUtau and the C# suite drives <see cref="DawManager"/> against a fake plugin; two
    /// implementations that each agree with their own fake can still disagree with each other,
    /// and a wire format is exactly where they would.
    ///
    /// Opt-in, because it needs the C++ plugin built and running:
    ///
    /// <code>
    /// bridge-host --dir /tmp/bridge-live --rate 44100 --notes 4 --note-spacing 0.5 --note-length 0.25
    /// OPENUTAU_BRIDGE_DISCOVERY=/tmp/bridge-live dotnet run --project OpenUtau.Test
    /// </code>
    ///
    /// Skipped when that variable is unset, so CI and everyday runs are unaffected.
    /// </summary>
    [Collection(DawIntegrationCollection.Name)]
    public class DawRealPluginMidiTest : IDisposable {
        private readonly UProject previousProject;
        private DawManager? manager;

        public DawRealPluginMidiTest() {
            previousProject = DocManager.Inst.TakeProjectForTest(BuildProject());
            // Applying notes goes through DocManager.ExecuteCmd, which otherwise posts to a UI
            // thread that does not exist here.
            DocManager.Inst.CommandSink = cmd => cmd.Execute();
        }

        public void Dispose() {
            DocManager.Inst.CommandSink = null!;
            DocManager.Inst.TakeProjectForTest(previousProject);
            manager?.Dispose();
        }

        /// <summary>
        /// One empty track. Recording into an empty track is the case that also exercises part
        /// creation, and nothing here needs rendered audio: this test is about notes crossing the
        /// wire, not about the audio plane.
        /// </summary>
        private static UProject BuildProject() {
            var built = new UProject();
            built.FilePath = Path.Combine(Path.GetTempPath(), "daw-real-midi.ustx");
            built.tracks.Clear();
            built.tracks.Add(new UTrack("Record") { TrackNo = 0, Volume = 0, Pan = 0 });
            built.timeAxis.BuildSegments(built);
            return built;
        }

        [Fact]
        public async Task RealPluginRecordsNotesIntoTheProject() {
            string? directory = Environment.GetEnvironmentVariable("OPENUTAU_BRIDGE_DISCOVERY");
            if (string.IsNullOrEmpty(directory)) {
                Assert.Skip(
                    "Set OPENUTAU_BRIDGE_DISCOVERY to a running bridge-host's discovery directory " +
                    "to run this live recording test.");
            }
            Assert.True(Directory.Exists(directory), $"No such directory: {directory}");

            // Discovery is exercised for real: the file was written by the C++ side, and this is
            // the shipping scanner reading it.
            var servers = new DawServerFinder(directory!).Scan();
            var server = Assert.Single(servers);
            Assert.True(server.IsCompatible, $"Advertised api {server.Info.ApiVersion}.");
            // The feature only exists from 1.3 on; a lower minor means the plugin is stale.
            Assert.True(DawApiVersion.TryParse(server.Info.ApiVersion, out var advertised));
            Assert.True(advertised.Minor >= 3,
                $"The bridge advertises {server.Info.ApiVersion}, which has no MIDI input (§6.3).");

            var project = DocManager.Inst.Project;
            manager = new DawManager(null, null, null) {
                UseTimerPump = false,
                ProjectSource = () => project,
            };
            await manager.ConnectAsync(server);
            Assert.Equal(DawConnectionState.Connected, manager.State);

            // A window wide enough to contain wherever the host's transport happens to be.
            // bridge-host plays forward from process start, so by the time this connects its
            // position is however many seconds it has been running - not something this test can
            // predict. A narrow window would filter out every note and look like a protocol
            // failure. In real use the window is the region the user selected, so it brackets the
            // transport by construction.
            const double windowStartMs = 0;
            const double windowEndMs = 3_600_000;
            var sessionId = await manager.StartMidiRecordingAsync(0, windowStartMs, windowEndMs);
            Assert.False(string.IsNullOrEmpty(sessionId));

            // Let the host play into the session for long enough to cross several note
            // boundaries, then stop once. Stopping immediately would close the session within a
            // block or two of opening it, so at most one note-on could land - and a note-on whose
            // note-off never arrives is closed at the window's end, which is a legitimate but
            // unrepresentative result to assert against.
            await Task.Delay(TimeSpan.FromSeconds(3));
            int applied = await manager.StopMidiRecordingAsync();

            Assert.True(applied > 0,
                "No notes arrived from the real plugin. Is bridge-host running with --notes?");

            var part = Assert.Single(project.parts.OfType<UVoicePart>());
            Assert.Equal(0, part.trackNo);
            Assert.Equal(applied, part.notes.Count);
            // The pattern ascends from middle C and repeats every cycle, so tones are a run
            // starting at 60 that may wrap: a recording longer than one cycle sees 60 again.
            // Asserting the whole sequence would pin the test to how many cycles happened to
            // elapse, which is timing, not protocol.
            var tones = part.notes.Select(n => n.tone).OrderBy(t => t).ToList();
            Assert.All(tones, t => Assert.InRange(t, 60, 71));
            Assert.Contains(60, tones);
            // MIDI carries no lyric, so every note gets the placeholder (§6.3.2).
            Assert.All(part.notes, n => Assert.Equal(DawManager.DefaultDawLyric, n.lyric));
            // Positions ascend with the pattern. The same tone recurs across cycles, so the
            // check is that notes are spread out in time rather than all stacked at one point.
            var positions = part.notes.Select(n => n.position).OrderBy(p => p).ToList();
            Assert.True(positions.Last() > positions.First(),
                "The recorded notes are all at one position, so timing was lost in transit.");

            await manager.DisconnectAsync();
            Assert.False(manager.IsConnected);
        }
    }
}
