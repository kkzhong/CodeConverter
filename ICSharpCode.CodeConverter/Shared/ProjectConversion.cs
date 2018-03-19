﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ICSharpCode.CodeConverter.CSharp;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Text;

namespace ICSharpCode.CodeConverter.Shared
{
    public class ProjectConversion<TLanguageConversion> where TLanguageConversion : ILanguageConversion, new()
    {
        private bool _methodBodyOnly;
        private Compilation _sourceCompilation;
        private IEnumerable<SyntaxTree> _syntaxTreesToConvert;
        private static readonly AdhocWorkspace AdhocWorkspace = new AdhocWorkspace();
        private readonly ConcurrentDictionary<string, Exception> _errors = new ConcurrentDictionary<string, Exception>();
        private readonly Dictionary<string, SyntaxTree> _firstPassResults = new Dictionary<string, SyntaxTree>();
        private readonly TLanguageConversion _languageConversion;

        private ProjectConversion(Compilation sourceCompilation, string solutionDir)
            : this(sourceCompilation, sourceCompilation.SyntaxTrees.Where(t => t.FilePath.StartsWith(solutionDir)))
        {
        }

        private ProjectConversion(Compilation sourceCompilation, IEnumerable<SyntaxTree> syntaxTreesToConvert)
        {
            _languageConversion = new TLanguageConversion();
            _sourceCompilation = sourceCompilation;
            _syntaxTreesToConvert = syntaxTreesToConvert;
        }

        public static ConversionResult ConvertText(string text, IReadOnlyCollection<MetadataReference> references)
        {
            var languageConversion = new TLanguageConversion();
            var syntaxTree = languageConversion.CreateTree(text);
            var compilation = languageConversion.CreateCompilationFromTree(syntaxTree, references);
            return ConvertSingle(compilation, syntaxTree, new TextSpan(0, 0)).GetAwaiter().GetResult();
        }

        public static async Task<ConversionResult> ConvertSingle(Compilation compilation, SyntaxTree syntaxTree, TextSpan selected)
        {
            if (selected.Length > 0) {
                var annotatedSyntaxTree = await GetSyntaxTreeWithAnnotatedSelection(syntaxTree, selected);
                compilation = compilation.ReplaceSyntaxTree(syntaxTree, annotatedSyntaxTree);
                syntaxTree = annotatedSyntaxTree;
            }

            var conversion = new ProjectConversion<TLanguageConversion>(compilation, new [] {syntaxTree});
            return ConvertProject(conversion, true).Single();
        }

        public static IEnumerable<ConversionResult> ConvertProjects(IReadOnlyCollection<Project> projects)
        {
            var solutionDir = Path.GetDirectoryName(projects.First().Solution.FilePath);
            foreach (var project in projects) {
                var compilation = project.GetCompilationAsync().GetAwaiter().GetResult();
                var projectConversion = new ProjectConversion<TLanguageConversion>(compilation, solutionDir);
                foreach (var conversionResult in ConvertProject(projectConversion)) yield return conversionResult;
            }
        }

        private static IEnumerable<ConversionResult> ConvertProject(ProjectConversion<TLanguageConversion> projectConversion, bool extractSelection = false)
        {
            foreach (var pathNodePair in projectConversion.Convert())
            {
                var errors = projectConversion._errors.TryRemove(pathNodePair.Key, out var nonFatalException)
                    ? new[] {nonFatalException}
                    : new Exception[0];
                var resultNode = extractSelection ? GetSelectedNode(pathNodePair.Value) : pathNodePair.Value;
                yield return new ConversionResult(resultNode.ToFullString(), errors) { SourcePathOrNull = pathNodePair.Key };
            }

            foreach (var error in projectConversion._errors)
            {
                yield return new ConversionResult(error.Value) {SourcePathOrNull = error.Key};
            }
        }

        private Dictionary<string, SyntaxNode> Convert()
        {
            FirstPass();
            var secondPassByFilePath = SecondPass();
            return secondPassByFilePath;
        }

        private Dictionary<string, SyntaxNode> SecondPass()
        {
            var secondPassByFilePath = new Dictionary<string, SyntaxNode>();
            foreach (var firstPassResult in _firstPassResults) {
                var treeFilePath = firstPassResult.Key;
                try {
                    secondPassByFilePath.Add(treeFilePath, SingleSecondPass(firstPassResult));
                }  catch (Exception e) {
                    secondPassByFilePath.Add(treeFilePath, Formatter.Format(firstPassResult.Value.GetRoot(), AdhocWorkspace));
                    _errors.TryAdd(treeFilePath, e);
                }
            }
            return secondPassByFilePath;
        }

        private SyntaxNode SingleSecondPass(KeyValuePair<string, SyntaxTree> cs)
        {
            var secondPassNode = _languageConversion.SingleSecondPass(cs);
            if (_methodBodyOnly) secondPassNode = _languageConversion.RemoveSurroundingClassAndMethod(secondPassNode);
            return Formatter.Format(secondPassNode, AdhocWorkspace);
        }

        private void FirstPass()
        {
            foreach (var tree in _syntaxTreesToConvert)
            {
                var treeFilePath = tree.FilePath ?? "";
                try {
                    SingleFirstPass(tree, treeFilePath);
                }
                catch (NotImplementedOrRequiresSurroundingMethodDeclaration)
                    when (!_methodBodyOnly && _sourceCompilation.SyntaxTrees.Count() == 1)
                {
                    SingleFirstPassSurroundedByClassAndMethod(tree);
                }
                catch (Exception e)
                {
                    _errors.TryAdd(treeFilePath, e);
                }
            }
        }

        private void SingleFirstPass(SyntaxTree tree, string treeFilePath)
        {
            var convertedTree = _languageConversion.SingleFirstPass(_sourceCompilation, tree);
            _firstPassResults.Add(treeFilePath, convertedTree);
        }

        private void SingleFirstPassSurroundedByClassAndMethod(SyntaxTree tree)
        {
            var newTree = _languageConversion.CreateTree(_languageConversion.WithSurroundingClassAndMethod(tree.GetText().ToString()));
            _methodBodyOnly = true;
            _sourceCompilation = _sourceCompilation.AddSyntaxTrees(newTree);
            _syntaxTreesToConvert = new[] {newTree};
            Convert();
        }

        private static async Task<SyntaxTree> GetSyntaxTreeWithAnnotatedSelection(SyntaxTree syntaxTree, TextSpan selected)
        {
            var root = await syntaxTree.GetRootAsync();
            var selectedNode = root.FindNode(selected);
            var annotatatedNode = selectedNode.WithAdditionalAnnotations(new SyntaxAnnotation(TriviaConverter.SelectedNodeAnnotationKind));
            return root.ReplaceNode(selectedNode, annotatatedNode).SyntaxTree.WithFilePath(syntaxTree.FilePath);
        }

        private static SyntaxNode GetSelectedNode(SyntaxNode resultNode)
        {
            var annotatedNode = resultNode.GetAnnotatedNodes(TriviaConverter.SelectedNodeAnnotationKind).SingleOrDefault();
            return annotatedNode == null ? resultNode : Formatter.Format(annotatedNode, AdhocWorkspace);
        }
    }
}