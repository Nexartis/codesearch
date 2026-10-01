using System.Text.Json;
using Xunit;
using ScipCsharp;

namespace ScipCsharp.Tests;

/// <summary>
/// Pins the JSON wire contract of <see cref="FindRefsOutput"/> against the
/// production serializer (<see cref="OutputWriter"/>): the Warnings list must
/// serialize under the snake_case <c>warnings</c> property, because that is
/// the key the Rust side parses
/// (<c>scip_parse::parse_find_refs_output</c>, <c>#[serde(default)] warnings</c>).
/// A rename or a changed naming policy would silently deserialize as
/// "complete" on the Rust side — a partial answer passing for a full one.
///
/// The AddRange sites that populate the list
/// (ReferenceResolver.BuildSymbolMapAsync / ResolveReferencesAsync) are
/// Roslyn-path glue covered by the csharp_helper_integration suite
/// (cargo test --features csharp_helper_integration); this test pins the
/// wire shape only.
/// </summary>
public class FindRefsWireContractTests
{
    [Fact]
    public async Task FindRefsOutput_Warnings_SerializeUnderTheSnakeCaseWarningsProperty()
    {
        var output = new FindRefsOutput
        {
            Symbol = "csharp MyApp . Calculator#Add(int, int).",
            References =
            [
                new FindRefsOccurrence { File = "src/Calc.cs", StartLine = 7, EndLine = 7, Kind = "reference" },
            ],
            Warnings =
            [
                "could not compile project 'Broken' — its symbols are missing from the map",
            ],
        };

        var path = Path.Combine(Path.GetTempPath(), $"findrefs-wire-{Guid.NewGuid():N}.json");
        try
        {
            // The exact serializer the find-refs subcommand ships with — not
            // hand-built options, which would not catch a change to
            // OutputWriter's own JsonSerializerOptions.
            await OutputWriter.WriteRefsAsync(output, path);
            var json = await File.ReadAllTextAsync(path);
            var parsed = JsonDocument.Parse(json);

            Assert.True(
                parsed.RootElement.TryGetProperty("warnings", out var warnings),
                $"the Warnings list must serialize as 'warnings' (SnakeCaseLower), got: {json}");
            Assert.Equal(1, warnings.GetArrayLength());
            Assert.Equal(
                "could not compile project 'Broken' — its symbols are missing from the map",
                warnings[0].GetString());

            // The rest of the contract parse_find_refs_output reads.
            Assert.Equal("2.0", parsed.RootElement.GetProperty("version").GetString());
            var occurrence = parsed.RootElement.GetProperty("references")[0];
            Assert.True(
                occurrence.TryGetProperty("start_line", out _),
                "occurrence line fields must stay snake_case");
        }
        finally
        {
            File.Delete(path);
        }
    }
}
