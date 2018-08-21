﻿using FluentAssertions;
using Microsoft.CodeAnalysis.Text;
using System.Collections.Immutable;
using System.Linq;
using System.Threading.Tasks;
using WorkspaceServer.Models.Execution;
using WorkspaceServer.Servers.Roslyn.Instrumentation;
using WorkspaceServer.Tests.Servers.Roslyn.Instrumentation;
using WorkspaceServer.Transformations;
using Xunit;
using SpanDictionary = System.Collections.Generic.IDictionary<string, System.Collections.Generic.IEnumerable<Microsoft.CodeAnalysis.Text.LinePositionSpan>>;

namespace WorkspaceServer.Tests.Servers.Roslyn.Instrumentation
{
    public class InstrumentationLineMapperTests
    {
        private (AugmentationMap,
            VariableLocationMap,
            Microsoft.CodeAnalysis.Document,
            Viewport,
            SpanDictionary
            ) Setup(string viewportCodeMarkup)
        {
            MarkupTestFile.GetNamedSpans(viewportCodeMarkup, out var viewportCode, out var textSpans);
            var code = $@"
using System;
using System.Linq;
namespace RoslynRecorder
{{
    class Program
    {{
        static void Main(string[] args)
        {{
        int thisShouldBeIgnored = 1;
#region test
        {viewportCode}
#endregion
        }}
    }}
}}
";
            var linePositionSpans = textSpans.ToDictionary(
                kv => kv.Key,
                kv => kv.Value.Select(span => TextSpanExtensions.ToLinePositionSpan(span, SourceText.From(viewportCode))
                    )
                );

            var withLF = CodeManipulation.EnforceLF(code);
            var document = Sources.GetDocument(withLF);
            var workspace = new Workspace(files: new[] { new Workspace.File("test.cs", withLF) });
            var visitor = new InstrumentationSyntaxVisitor(document);
            var viewport = new BufferInliningTransformer().ExtractViewPorts(workspace).DefaultIfEmpty(null).First();

            return (visitor.Augmentations, visitor.VariableLocations, document, viewport, linePositionSpans);
        }

        [Fact]
        public async Task MapLineLocationsRelativeToViewport_Does_Nothing_Without_Viewport()
        {
            var (augmentation, locations, document, _, _) = Setup(@"Console.WriteLine(""hello world"");");
            var (newAugmentation, newLocations) = await InstrumentationLineMapper.MapLineLocationsRelativeToViewportAsync(augmentation, locations, document);

            augmentation.Should().BeEquivalentTo(newAugmentation);
            locations.Should().BeEquivalentTo(newLocations);
        }

        [Fact]
        public async Task MapLineLocationsRelativeToViewport_Maps_Augmentation_FilePosition_Correctly()
        {
            var (augmentation, locations, document, viewport, spans) = Setup(
@"
{|a:int a = 0;|}
{|b:Console.WriteLine(""Entry Point"");|}
"
                );

            var (newAugmentation, newLocations) = await InstrumentationLineMapper.MapLineLocationsRelativeToViewportAsync(augmentation, locations, document, viewport);

            var linePositions = newAugmentation.Data.Values.Select(state => state.CurrentFilePosition.Line);
            var expectedLinePositions = spans.Values.Select(span => span.First().Start.Line);
            linePositions.Should().Equal(expectedLinePositions);
        }

        [Fact]
        public async Task MapLineLocationsRelativeToViewport_Maps_Variable_Location_Correctly()
        {

            var (augmentation, locations, document, viewport, spans) = Setup(
                @"
{|a:int a = 0;|}
{|b:Console.WriteLine(""Entry Point"");|}
");

            var (newAugmentation, newLocations) = await InstrumentationLineMapper.MapLineLocationsRelativeToViewportAsync(augmentation, locations, document, viewport);
            var variableLocationLines = newLocations.Data.Values
                .SelectMany(locs => locs)
                .Select(loc => loc.StartLine);
            var expectedLocations = spans["a"].First().Start.Line;

            variableLocationLines.Should().Equal(new[] { expectedLocations });
        }
        
        [Fact]
        public async Task MapLineLocationsRelativeToViewport_Maps_Variable_At_Viewport_End_Correctly()
        {
            var (augmentation, locations, document, viewport, spans) = Setup(
                @"
{|b:Console.WriteLine(""Entry Point"");|}
{|a:int a = 0;|}
");

            var (newAugmentation, newLocations) = await InstrumentationLineMapper.MapLineLocationsRelativeToViewportAsync(augmentation, locations, document, viewport);
            var variableLocationLines = newLocations.Data.Values
                .SelectMany(locs => locs)
                .Select(loc => loc.StartLine);
            var expectedLocations = spans["a"].First().Start.Line;

            variableLocationLines.Should().Equal(new[] { expectedLocations });
        }


        [Fact]
        public async Task MapLineLocationsRelativeToViewport_Maps_Multiple_Variables_On_Single_Line_Correctly()
        {
            var (augmentation, locations, document, viewport, spans) = Setup(
                @"
{|variables:var (a, b) = (1, 2);|}
");

            var (newAugmentation, newLocations) = await InstrumentationLineMapper.MapLineLocationsRelativeToViewportAsync(augmentation, locations, document, viewport);
            var variableLocationLines = newLocations.Data.Values
                .SelectMany(locs => locs)
                .Select(loc => loc.StartLine)
                .Distinct();
            var expectedLocations = spans["variables"].First().Start.Line;

            variableLocationLines.Should().Equal(new[] { expectedLocations });
        }

        [Fact]
        public void FilterActiveViewport_Should_Return_Viewport_In_ActiveBufferId()
        {
            var text = CodeManipulation.EnforceLF(@"
using System;
namespace RoslynRecorder
{
    class Program
    {
        static void Main(string[] args)
        {
{|regionStart:#region test|}
            int a = 0;
            Console.WriteLine(""Entry Point"");
{|regionEnd:#endregion|}
        }
#region notthis
    }
#endregion
}");
            MarkupTestFile.GetNamedSpans(text, out var code, out var spans);
            var workspace = new Workspace(files: new[] { new Workspace.File("testFile.cs", code) });
            var viewports = new BufferInliningTransformer().ExtractViewPorts(workspace);
            var activeViewport = InstrumentationLineMapper.FilterActiveViewport(viewports, "testFile.cs@test").First();
            activeViewport.Region.Start.Should().Be(spans["regionStart"].First().End);
            activeViewport.Region.End.Should().Be(spans["regionEnd"].First().Start);
        }

        [Fact]
        public void FilterActiveViewport_Should_Return_Empty_Array_If_No_Regions()
        {
            var text = CodeManipulation.EnforceLF(Sources.simple);
            var workspace = new Workspace(files: new[] { new Workspace.File("testFile.cs", text) });
            var viewports = new BufferInliningTransformer().ExtractViewPorts(workspace);
            var activeViewport = InstrumentationLineMapper.FilterActiveViewport(viewports, "testFile.cs@test");
            activeViewport.Should().BeEmpty();
        }
    }
}
