using System;
using System.Collections.Generic;
using System.Linq;
using OpenUtau.Core.Ustx;

namespace OpenUtau.Core.DawIntegration {
    /// <summary>
    /// Applies a batch of DAW-recorded notes to the project (PROTOCOL.md §6.3).
    /// </summary>
    /// <remarks>
    /// Kept apart from <see cref="DawManager"/> so the conversion and its edge cases can be
    /// tested without a socket, a scheduler or a document thread.
    ///
    /// The two things that make this more than a loop:
    ///
    /// 1. **Milliseconds to ticks.** Notes arrive in the one coordinate both sides share
    ///    (§6.3.1). Converting with the project's own time axis does not correct a tempo
    ///    mismatch — §1 lists tempo-map sync as a non-goal — so a DAW running at a different
    ///    tempo places notes at musically wrong ticks. The existing bpm guard is the only
    ///    warning; this code must not pretend otherwise.
    ///
    /// 2. **Degenerate notes.** A DAW can hand over a note with zero or negative duration
    ///    (a stray key tap, a note shorter than the conversion's resolution). OpenUtau's
    ///    renderer needs a positive duration, so a floor is applied rather than trusting
    ///    the input.
    /// </remarks>
    public static class DawNoteImporter {
        /// <summary>
        /// Shortest note this will create, in ticks. A quarter of a 480-tick beat is short
        /// enough to be inaudible as a rhythm yet long enough for the renderer to voice.
        /// </summary>
        public const int MinDurationTicks = 120;

        /// <summary>MIDI note numbers are 0-127; anything else is a malformed payload.</summary>
        public const int MinTone = 0;
        public const int MaxTone = 127;

        /// <summary>
        /// Converts DAW notes to <see cref="UNote"/>s on <paramref name="project"/>'s time axis.
        /// </summary>
        /// <param name="defaultLyric">
        /// Substituted when a note carries none. MIDI has no lyric field, so this is the normal
        /// case for a DAW-side editor; the user fills in real lyrics in OpenUtau (§6.3.2).
        /// </param>
        /// <param name="windowStartMs">
        /// Rejects notes before this position. The plugin is supposed to drop them, but a note
        /// arriving from outside the session window is still a note the user did not record.
        /// </param>
        /// <param name="windowEndMs">Rejects notes starting at or after this position.</param>
        /// <returns>
        /// The notes that survived, sorted by position. Malformed entries are skipped rather
        /// than throwing: one bad note in a batch must not discard the rest.
        /// </returns>
        public static List<UNote> Convert(
            UProject project,
            IEnumerable<DawNote> notes,
            string defaultLyric,
            double windowStartMs,
            double windowEndMs) {
            var result = new List<UNote>();
            if (project == null || notes == null) {
                return result;
            }
            foreach (var note in notes) {
                if (note == null) {
                    continue;
                }
                if (note.Tone < MinTone || note.Tone > MaxTone) {
                    continue;
                }
                if (double.IsNaN(note.StartMs) || double.IsNaN(note.DurationMs)) {
                    continue;
                }
                // Half-open window: a note starting exactly at the end belongs to the next one.
                if (note.StartMs < windowStartMs || note.StartMs >= windowEndMs) {
                    continue;
                }
                int position = Math.Max(0, project.timeAxis.MsPosToTickPos(note.StartMs));
                int duration = project.timeAxis.MsPosToTickPos(note.StartMs + Math.Max(0, note.DurationMs)) - position;
                if (duration < MinDurationTicks) {
                    duration = MinDurationTicks;
                }
                var converted = new UNote {
                    position = position,
                    duration = duration,
                    tone = note.Tone,
                    lyric = string.IsNullOrWhiteSpace(note.Lyric) ? defaultLyric : note.Lyric!,
                };
                if (note.Velocity.HasValue) {
                    // Velocity is a note expression, not a field (see Format/VSQx.cs for the
                    // same construction when importing a VSQx). OpenUtau's `vel` runs 0-200
                    // with 100 as neutral, so a MIDI 0-127 value maps onto that scale with
                    // the neutral point preserved: 64 (MIDI's own centre) becomes 100.
                    converted.phonemeExpressions.Add(new UExpression(Format.Ustx.VEL) {
                        index = 0,
                        value = Math.Clamp(note.Velocity.Value, 0, 127) * 100 / 64,
                    });
                }
                result.Add(converted);
            }
            result.Sort((a, b) => a.position.CompareTo(b.position));
            return result;
        }

        /// <summary>
        /// Finds the part that should receive <paramref name="notes"/> on
        /// <paramref name="trackNo"/>, or null when the notes fall outside every part.
        /// </summary>
        /// <remarks>
        /// The first note's position picks the part, and the rest follow it there. Notes
        /// straddling two parts are not split: a recorded phrase is one musical gesture and
        /// belongs in one place, and splitting would silently change how it is voiced.
        /// </remarks>
        public static UVoicePart? FindTargetPart(UProject project, int trackNo, List<UNote> notes) {
            if (project == null || notes == null || notes.Count == 0) {
                return null;
            }
            if (trackNo < 0 || trackNo >= project.tracks.Count) {
                return null;
            }
            int position = notes[0].position;
            return project.parts
                .OfType<UVoicePart>()
                .FirstOrDefault(part => part.trackNo == trackNo
                    && position >= part.position
                    && position < part.position + part.Duration);
        }
    }
}
