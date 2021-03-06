﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;

namespace Whomst
{
    public class WhomstGlobalJob : IWhomstGlobals
    {
        public ScriptState ScriptState = null;

        public readonly HashSet<string> InputFiles = new HashSet<string>();
        public readonly HashSet<string> OutputFiles = new HashSet<string>();
        public readonly HashSet<string> Includes = new HashSet<string>();
        public readonly HashSet<string> Assemblies = new HashSet<string>();
        public readonly HashSet<string> Usings = new HashSet<string>();
        public readonly List<WhomstJob> jobWriteList = new List<WhomstJob>();
        public readonly Stack<WhomstJob> jobStack = new Stack<WhomstJob>();

        public IWhomstGlobals Globals => this;

        public WhomstJob CurrentJob => jobStack.Peek();

        #region IWhomstGlobals implementation
        IWhomstGlobals IWhomstGlobals.Globals => this;
        IList<string> IWhomstGlobals.PrevCodeLines => CurrentJob.PrevCodeLines;
        IList<string> IWhomstGlobals.PrevContentLines => CurrentJob.PrevContentLines;
        IList<string> IWhomstGlobals.PrevOutputLines => CurrentJob.PrevOutputLines;
        string IWhomstGlobals.PrevCode => CurrentJob.PrevCodeLines.AggLines(CurrentJob.FileCfg.FormatCfg.NewL);
        string IWhomstGlobals.PrevContent => CurrentJob.PrevContentLines.AggLines(CurrentJob.FileCfg.FormatCfg.NewL);
        string IWhomstGlobals.PrevOutput => CurrentJob.PrevOutputLines.AggLines(CurrentJob.FileCfg.FormatCfg.NewL);
        TextWriter IWhomstGlobals.WhomstOut => CurrentJob.WhomstOut;
        string IWhomstGlobals.AtString(string s) => Util.AtString(s, CurrentJob.FileCfg.FormatCfg.NewL);
        IDictionary<string, object> IWhomstGlobals.Defines => CurrentJob.FileCfg.Defines;
        OneTimeScriptState IWhomstGlobals.WhomstEval(string code, string src, int ln) =>
            new OneTimeScriptState(Eval(code, src, ln), src, ln);
        FileCfg IWhomstGlobals.FileConfig => CurrentJob.SharedFileCfg.Clone();
        StackCfg IWhomstGlobals.StackConfig => CurrentJob.SharedStackCfg.Clone();
        
        OneTimeScriptState IWhomstGlobals.WhomstLoad(
            string inputFile, string outputFile, FileOp operations,
            string src, int ln)
        {
            var fileCfg = Globals.FileConfig.SetFile(inputFile, outputFile).SetOperations(operations);
            return Globals.WhomstLoad(fileCfg, Globals.StackConfig);
        }

        OneTimeScriptState IWhomstGlobals.WhomstLoad(FileCfg fileCfg, StackCfg stackCfg,
            string src, int ln)
        {
            RunJob(fileCfg, stackCfg ?? CurrentJob.StackCfg.Clone());
            return new OneTimeScriptState(ScriptState, src, ln);
        }
        #endregion

        public static void Execute(FileCfg fileCfg, StackCfg stackCfg)
        {
            var runner = new WhomstGlobalJob();
            runner.RunJob(fileCfg, stackCfg);
            runner.WriteFiles();
        }

        public void RunJob(FileCfg fileCfg, StackCfg stackCfg)
        {
            var job = new WhomstJob
            {
                SharedStackCfg = stackCfg,
                SharedFileCfg = fileCfg,
            };

            {
                job.StackCfg = StackCfg.Default().MergeOverWith(job.SharedStackCfg);
                job.FileCfg = job.StackCfg.GetProtoFileCfg(job.SharedFileCfg.InputFileExt)
                    .MergeOverWith(job.SharedFileCfg);
                job.FileCfg.Validate();
                jobStack.Push(job);
                jobWriteList.Add(job);

                if (ScriptState == null)
                {
                    ScriptState = CSharpScript.RunAsync(
                        "",
                        ScriptOptions.Default
                            .WithMetadataResolver(ScriptMetadataResolver.Default
                                .WithBaseDirectory(Environment.CurrentDirectory)
                                .WithSearchPaths(RuntimeEnvironment.GetRuntimeDirectory())
                                .WithSearchPaths(job.StackCfg.Includes)
                            ).WithEmitDebugInformation(true),
                        this, typeof(IWhomstGlobals)).Result;
                }
                else
                {
                    foreach (var incl in job.StackCfg.Includes.Where(i => Includes.Add(i)))
                    {
                        Util.AssertWarning(false,
                            $"Could not add include directory {incl} midway through Whomst execution");
                    }
                }

                StringBuilder setupCode = new StringBuilder();

                foreach (var assm in job.StackCfg.Assemblies.Where(a => Assemblies.Add(a)))
                {
                    setupCode.AppendLine($"#r\"{assm}\"");
                }

                foreach (var using1 in job.StackCfg.Usings.Where(u => Usings.Add(u)))
                {
                    setupCode.AppendLine($"using {using1};");
                }

                Util.AssertWarning(
                    InputFiles.Add(job.FileCfg.InputFilePath),
                    $"{job.FileCfg.InputFilePath} is getting preprocessed twice.");

                Util.AssertWarning(
                    OutputFiles.Add(job.FileCfg.OutputFilePath),
                    $"{job.FileCfg.OutputFilePath} is getting written twice.");

                ScriptState = ScriptState.ContinueWithAsync(setupCode.ToString()).Result;
            }

            var fmt = job.FileCfg.FormatCfg;

            {
                var text = File.ReadAllText(job.FileCfg.InputFilePath, fmt.Encoding);
                var linesIn = text.EzSplit(fmt.NewL);

                void AddLineGroup(int s, int e, LineGroupType t) =>
                    job.LineGroups.Add(new LineGroup { InclStart = s, ExclEnd = e, Type = t });

                AddLineGroup(0, -1, LineGroupType.Content);

                for (int index = 0; index < linesIn.Length; index++)
                {
                    var line = linesIn[index];
                    
                    void Assert(bool b, string s) => Util.Assert(
                        b, s,
                        job.FileCfg.InputFileName, index + 1);

                    switch (job.LineGroups.Last().Type)
                    {
                        case LineGroupType.Content:
                            var startTokenInd = line.IndexOf(fmt.StartCodeToken);
                            if (0 <= startTokenInd)
                            {
                                job.LineGroups.Last().ExclEnd = index;
                                var endTokenInd = line.IndexOf(fmt.StartOutputToken);
                                if (0 <= endTokenInd)
                                {
                                    Assert(startTokenInd < endTokenInd, 
                                        $"{fmt.StartOutputToken} came before {fmt.StartCodeToken}");
                                    AddLineGroup(index, index + 1, LineGroupType.TwoLiner);
                                    AddLineGroup(index + 1, -1, LineGroupType.GenOutput);
                                }
                                else
                                {
                                    AddLineGroup(index, index + 1, LineGroupType.StartCode);
                                    AddLineGroup(index + 1, -1, LineGroupType.WhomstCode);
                                }
                            }

                            var startOneLinerInd = line.IndexOf(fmt.OneLinerStartCodeToken);

                            if (0 <= startOneLinerInd && (startTokenInd < 0))
                            {
                                var midOneLinerInd = line.IndexOf(fmt.OneLinerStartOutputToken);
                                var endOneLinerInd = line.IndexOf(fmt.OneLinerEndOutputToken);

                                Assert(
                                    startOneLinerInd < midOneLinerInd,
                                    $"{fmt.OneLinerStartCodeToken} must be followed with {fmt.OneLinerStartOutputToken}");
                                Assert(
                                    midOneLinerInd < endOneLinerInd,
                                    $"{fmt.OneLinerStartOutputToken} must be followed with {fmt.OneLinerEndOutputToken}");

                                job.LineGroups.Last().ExclEnd = index;
                                AddLineGroup(index, index + 1, LineGroupType.OneLiner);
                                AddLineGroup(index + 1, -1, LineGroupType.Content);
                            }
                            break;
                        case LineGroupType.WhomstCode:
                            if (line.Contains(fmt.StartOutputToken))
                            {
                                job.LineGroups.Last().ExclEnd = index;
                                AddLineGroup(index, index + 1, LineGroupType.StartOutput);
                                AddLineGroup(index + 1, -1, LineGroupType.GenOutput);
                            }
                            break;
                        case LineGroupType.GenOutput:
                            if (line.Contains(fmt.EndOutputToken))
                            {
                                job.LineGroups.Last().ExclEnd = index;
                                AddLineGroup(index, index + 1, LineGroupType.EndOutput);
                                AddLineGroup(index + 1, -1, LineGroupType.Content);
                            }
                            break;
                        default:
                            throw new ArgumentOutOfRangeException();
                    }
                }

                job.LineGroups.Last().ExclEnd = linesIn.Length;
                var endingType = job.LineGroups.Last().Type;

                Util.Assert(
                    endingType == LineGroupType.Content || endingType == LineGroupType.OneLiner,
                    $"Document ends with unclosed {endingType} (missing \"{fmt.EndOutputToken}\"??)",
                    job.FileCfg.InputFileName, job.LineGroups.Last().InclStart + 1);

                List<LineGroup> remove = new List<LineGroup>();

                foreach (var lg in job.LineGroups)
                {
                    lg.InLines = linesIn.Slice(lg.InclStart, lg.ExclEnd);
                    //if (lg.InLines.Count == 0) { remove.Add(lg); }
                }

                foreach (var rem in remove)
                {
                    job.LineGroups.Remove(rem);
                }
            }

            {
                string indent = null;
                string oldHash = null;

                foreach (var lineGroup in job.LineGroups)
                {
                    string firstLine = lineGroup.InLines.FirstOrDefault();

                    switch (lineGroup.Type)
                    {
                        case LineGroupType.Content:
                        case LineGroupType.OneLiner:
                        {
                            lineGroup.Indent = "";
                            lineGroup.DeIndentedInLines = lineGroup.InLines;
                        } break;
                        case LineGroupType.StartCode:
                        {
                            var tokenInd = firstLine.IndexOf(fmt.StartCodeToken);
                            Util.Assert(
                                tokenInd + fmt.StartCodeToken.Length == firstLine.Length,
                                $"Line cannot contain anything after {fmt.StartCodeToken}",
                                job.FileCfg.InputFileName, lineGroup.StartingLineNumber);
                            indent = firstLine.Substring(0, tokenInd);
                            
                        } break;
                        case LineGroupType.TwoLiner:
                        {
                            var tokenInd = firstLine.IndexOf(fmt.StartCodeToken);
                            indent = firstLine.Substring(0, tokenInd);
                        } break;
                        case LineGroupType.WhomstCode: {
                        } break;
                        case LineGroupType.StartOutput: {
                            Util.Assert(
                                firstLine == $"{indent}{fmt.StartOutputToken}",
                                "Line with {fmt.StartOutputToken} should contain it, indent, and nothign else",
                                job.FileCfg.InputFileName, lineGroup.StartingLineNumber);
                        } break;
                        case LineGroupType.GenOutput: {
                            lineGroup.Indent = Regex.Replace(indent, @"[^\s]", " ");
                        } break;
                        case LineGroupType.EndOutput: {
                            Util.Assert(
                                firstLine.StartsWith($"{indent}{fmt.EndOutputToken}"),
                                "Indent mismatch",
                                job.FileCfg.InputFileName, lineGroup.StartingLineNumber);
                            string restOfLine = firstLine.Substring(indent.Length + fmt.EndOutputToken.Length).Trim();

                            Util.Assert(!restOfLine.StartsWith(fmt.HashInfix) || restOfLine.Contains(oldHash),
                                "Code preceding hash does not match hash, which means that generated code has been edited.\n" +
                                "Undo changes or remove Hash",
                                job.FileCfg.InputFileName, lineGroup.StartingLineNumber);
                        } break;
                        default:
                            throw new ArgumentOutOfRangeException();
                    }

                    lineGroup.Indent = lineGroup.Indent ?? indent;
                    lineGroup.DeIndentedInLines = lineGroup.DeIndentedInLines
                        ?? lineGroup.InLines.Select((l, ind) =>
                        {
                            Util.Assert(l.StartsWith(lineGroup.Indent), "Line not indented",
                                job.FileCfg.InputFileName, lineGroup.StartingLineNumber + ind);
                            return l.Substring(lineGroup.Indent.Length);
                        }).ToList();

                    if (lineGroup.Type == LineGroupType.GenOutput)
                    {
                        oldHash = Util.CalculateMD5Hash(lineGroup.DeIndentedInLines.AggLines(fmt.NewL));
                    }
                }
            }

            {
                string RunAndRtrn(string code, string file, int lineNumber)
                {
                    ScriptState = Eval(code, file, lineNumber);
                    var returnValue = ScriptState.ReturnValue;
                    var returnValueScriptState = returnValue as OneTimeScriptState;

                    // WhomstLoad or WhomstEval
                    if (returnValueScriptState != null)
                    {
                        ScriptState = returnValueScriptState.ScriptState;
                    }
                    else if (returnValue != null)
                    {
                        return returnValue.ToString();
                    }

                    return null;
                }

                int prevCodeStartLine = -1;

                foreach (var lineGroup in job.LineGroups)
                {
                    switch (lineGroup.Type)
                    {
                        case LineGroupType.Content: {
                            job.PrevContentLines = lineGroup.InLines;
                        } break;
                        case LineGroupType.OneLiner:
                        {
                            var firstLine = lineGroup.InLines[0];
                            var startInd = firstLine.IndexOf(fmt.OneLinerStartCodeToken);
                            var midInd = firstLine.IndexOf(fmt.OneLinerStartOutputToken);
                            var endInd = firstLine.IndexOf(fmt.OneLinerEndOutputToken);
                            var startIndRight = startInd + fmt.OneLinerStartCodeToken.Length;
                            var midIndRight = midInd + fmt.OneLinerStartOutputToken.Length;
                            var endIndRight = endInd + fmt.OneLinerEndOutputToken.Length;
                            var code = firstLine.Substring(startIndRight, midInd - startIndRight);
                            var oldOutput = firstLine.Substring(midIndRight, endInd - midIndRight);
                            var output = RunAndRtrn(code, job.FileCfg.InputFileName, lineGroup.StartingLineNumber)
                                    ?.Replace(fmt.NewL, ", ");
                            // TODO this is shameful
                            lineGroup.DeIndentedOutLines = new[]
                            {
                                firstLine.Substring(0, startInd),
                                code,
                                output,
                                firstLine.Substring(endIndRight, firstLine.Length - endIndRight),
                            };
                            // TODO this is shameful
                            lineGroup.DeIndentedInLines = new[]
                            {
                                firstLine.Substring(0, startInd),
                                code,
                                oldOutput ?? "",
                                firstLine.Substring(endIndRight, firstLine.Length - endIndRight),
                            };
                        } break;
                        case LineGroupType.StartCode: {
                            lineGroup.DeIndentedOutLines = fmt.StartCodeToken.Array1();
                        } break;
                        case LineGroupType.TwoLiner:
                        {
                            var firstLine = lineGroup.InLines[0];
                            var startInd = firstLine.IndexOf(fmt.StartCodeToken);
                            var midInd = firstLine.IndexOf(fmt.StartOutputToken);
                            var startIndRight = startInd + fmt.StartCodeToken.Length;
                            var code = firstLine.Substring(startIndRight, midInd - startIndRight);
                            lineGroup.DeIndentedOutLines = code.Array1();
                            job.PrevCodeLines = lineGroup.DeIndentedOutLines;
                            prevCodeStartLine = lineGroup.StartingLineNumber;
                        } break;
                        case LineGroupType.WhomstCode: {
                            job.PrevCodeLines = lineGroup.DeIndentedInLines;
                            prevCodeStartLine = lineGroup.StartingLineNumber;
                        } break;
                        case LineGroupType.StartOutput:
                            break;
                        case LineGroupType.GenOutput: {
                            // TODO past output
                            job.WhomstOut = new StringWriter();
                            var codeSb = new StringBuilder();
                            int lineNumber = prevCodeStartLine;
                            
                            foreach (var ind in Enumerable.Range(0, job.PrevCodeLines.Count))
                            {
                                var lineCpy = job.PrevCodeLines[ind];

                                if (lineCpy.Contains(fmt.ForceDirective))
                                {
                                    lineCpy = lineCpy.Replace(fmt.ForceDirective, "");
                                }
                                else if (job.FileCfg.ShallSkipCompute) { continue; }

                                if (lineCpy.Contains(fmt.YieldDirective) || ind == job.PrevCodeLines.Count - 1)
                                {
                                    codeSb.AppendLine(lineCpy.Replace(fmt.YieldDirective, ""));
                                    var output = RunAndRtrn(codeSb.ToString(), fileCfg.InputFileName, lineNumber);
                                    if (output != null) { job.WhomstOut.WriteLine(output); }
                                    codeSb.Clear();
                                    lineNumber = prevCodeStartLine + ind + 1;
                                }
                                else { codeSb.AppendLine(lineCpy); }
                            }

                            var outputUnindented = job.WhomstOut.GetStringBuilder().ToString();

                            if (outputUnindented.EndsWith(Environment.NewLine))
                            {
                                outputUnindented =
                                    outputUnindented.Substring(0, outputUnindented.Length - Environment.NewLine.Length);
                            }

                            job.WhomstOut.Dispose();
                            job.WhomstOut = null;
                            lineGroup.DeIndentedOutLines = outputUnindented.EzSplit(fmt.NewL).ToList();
                            job.PrevOutputLines = lineGroup.DeIndentedOutLines;
                        } break;
                        case LineGroupType.EndOutput:
                        {
                            var hash = Util.CalculateMD5Hash(job.PrevOutputLines.AggLines(fmt.NewL));
                            lineGroup.DeIndentedOutLines = $"{fmt.EndOutputToken}{fmt.HashInfix} {hash}".Array1();
                        } break;
                        default:
                            throw new ArgumentOutOfRangeException();
                    }
                    lineGroup.DeIndentedOutLines = lineGroup.DeIndentedOutLines ?? lineGroup.DeIndentedInLines;
                    OneTimeScriptState.CheckInstancesDisposed();
                }
            }

            // Invalidating stuff which I know I don't want to be used again
            job.WhomstOut = null;
            job.PrevCodeLines = null;
            job.PrevContentLines = null;
            job.PrevOutputLines = null;
            jobStack.Pop();
        }

        public void WriteFiles()
        {
            foreach (var job in jobWriteList)
            {
                bool deleteOutput = job.FileCfg.ShallDeleteOutput;
                bool deleteCode = job.FileCfg.ShallDeleteCode;
                bool generate = job.FileCfg.ShallGenerate;

                if (!deleteOutput && !deleteCode && !generate) { return; }

                var fmt = job.FileCfg.FormatCfg;

                using (var writer = new StreamWriter(job.FileCfg.OutputFilePath, false, fmt.Encoding))
                {
                    writer.NewLine = fmt.NewL;

                    foreach (var lineGroup in job.LineGroups)
                    {
                        IList<string> writtenUnindentLines = lineGroup.DeIndentedOutLines;

                        switch (lineGroup.Type) // TODO xLiner
                        {
                            case LineGroupType.Content:
                                break;
                            case LineGroupType.OneLiner:
                            {
                                Util.Assert(lineGroup.DeIndentedOutLines.Count == 4, "!!!");
                                var outSegs = lineGroup.DeIndentedOutLines;
                                var inSegs = lineGroup.DeIndentedInLines;
                                var sb = new StringBuilder($"{outSegs[0]}");
                                sb.Append(deleteCode ? "" : fmt.OneLinerStartCodeToken);
                                sb.Append(deleteCode ? "" : outSegs[1]);
                                sb.Append(deleteCode
                                    ? fmt.OneLinerColdStartOutputToken
                                    : fmt.OneLinerStartOutputToken);
                                sb.Append(deleteOutput
                                    ? ""
                                    : (generate ? outSegs[2] : inSegs[2]));
                                sb.Append(deleteCode ? fmt.OneLinerColdEndOutputToken : fmt.OneLinerEndOutputToken);
                                sb.Append(outSegs[3]);
                                writtenUnindentLines = sb.ToString().Array1();
                            } break;
                            case LineGroupType.TwoLiner:
                            {
                                var code = lineGroup.DeIndentedOutLines[0];
                                writtenUnindentLines = !deleteCode
                                    ? $"{fmt.StartCodeToken}{code}{fmt.StartOutputToken}".Array1()
                                    : fmt.ColdStartOutputToken.Array1();
                            } break;
                            case LineGroupType.StartCode:
                            {
                                if (deleteCode) { writtenUnindentLines = null; }
                            } break;
                            case LineGroupType.WhomstCode:
                            {
                                if (deleteCode) { writtenUnindentLines = null; }
                            } break;
                            case LineGroupType.StartOutput:
                            {
                                if (deleteCode)
                                {
                                    writtenUnindentLines = fmt.ColdStartOutputToken.Array1();
                                }
                            } break;
                            case LineGroupType.GenOutput:
                            {
                                if (!generate) { writtenUnindentLines = lineGroup.DeIndentedInLines; }
                                if (deleteOutput) { writtenUnindentLines = null; }
                            } break;
                            case LineGroupType.EndOutput:
                            {
                                // If user has elected not to generate, leave prev line (possibly including hash) alone
                                if (!generate) { writtenUnindentLines = lineGroup.DeIndentedInLines; }

                                // If user wants output deleted, strip hash value from line (no matter the source)
                                // Same if 
                                // This is an unusual case :/
                                if (deleteOutput || (job.FileCfg.InputFilePath != job.FileCfg.OutputFilePath))
                                {
                                    writtenUnindentLines = $"{fmt.EndOutputToken}".Array1();
                                }

                                // or if whomst code is deleted, replace with cold output token
                                if (deleteCode)
                                {
                                    writtenUnindentLines = writtenUnindentLines.Select(
                                        l => l.Replace(fmt.StartOutputToken, fmt.ColdStartOutputToken)).ToList();
                                }
                            } break;
                        }

                        bool lastGroup = lineGroup == job.LineGroups.Last();
                        if (writtenUnindentLines != null)
                        {
                            foreach (var ind in Enumerable.Range(0, writtenUnindentLines.Count))
                            {
                                if (lastGroup && (ind == writtenUnindentLines.Count - 1))
                                {
                                    writer.Write($"{lineGroup.Indent}{writtenUnindentLines[ind]}");
                                }
                                else
                                {
                                    writer.WriteLine($"{lineGroup.Indent}{writtenUnindentLines[ind]}");
                                }
                            }
                        }
                    }
                }
            }
        }

        public ScriptState Eval(string code, string fileName, int lineNumber)
        {
            try
            {
                code = $"#line {lineNumber} \"{fileName}\"\n{code}";
                return ScriptState.ContinueWithAsync(code).Result;
            }
            catch (AggregateException aggregateEx)
            {
                throw aggregateEx.InnerException ?? aggregateEx;
            }
        }
    }

    public enum LineGroupType
    {
        Content,
        StartCode,
        WhomstCode,
        StartOutput,
        GenOutput,
        EndOutput,
        TwoLiner,
        OneLiner,
    }

    public interface IWhomstGlobals
    {
        IWhomstGlobals Globals { get; }
        string PrevContent { get; }
        string PrevOutput { get; }
        string PrevCode { get; }
        IList<string> PrevContentLines { get; }
        IList<string> PrevOutputLines { get; }
        IList<string> PrevCodeLines { get; }
        IDictionary<string, object> Defines { get; }
        TextWriter WhomstOut { get; }
        string AtString(string s);
        OneTimeScriptState WhomstEval(
            string code,
            [CallerFilePath] string sourceFile = "UNKNOWN",
            [CallerLineNumber] int lineNumber = -1);
        FileCfg FileConfig { get; }
        StackCfg StackConfig { get; }
        OneTimeScriptState WhomstLoad(
            string inputFile, string outputFile = null, FileOp operations = FileOp.None,
            [CallerFilePath] string sourceFile = "UNKNOWN",
            [CallerLineNumber] int lineNumber = -1);
        OneTimeScriptState WhomstLoad(
            FileCfg fileCfg, StackCfg stackCfg = null,
            [CallerFilePath] string sourceFile = "UNKNOWN",
            [CallerLineNumber] int lineNumber = -1);
    }

    public class WhomstJob
    {
        // Long term state
        public readonly IList<LineGroup> LineGroups = new List<LineGroup>();

        // Changes between blocks
        public StringWriter WhomstOut;
        public IList<string> PrevCodeLines = new List<string>();
        public IList<string> PrevContentLines = new List<string>();
        public IList<string> PrevOutputLines = new List<string>();

        // Actually used in execution
        public FileCfg FileCfg;
        public StackCfg StackCfg;

        // Holds only non-default values
        public FileCfg SharedFileCfg;
        public StackCfg SharedStackCfg;
    }

    public class LineGroup
    {
        public IList<string> InLines;
        public IList<string> OutLines;
        public IList<string> DeIndentedInLines;
        public IList<string> DeIndentedOutLines;
        public LineGroupType Type;
        public int InclStart;
        public int ExclEnd = -1;
        public string Indent;

        public int StartingLineNumber => InclStart + 1;

        public override string ToString()
        {
            return $"{nameof(Type)}: {Type}, " +
                   $"{nameof(InLines)}: [{InLines.Count}]({InLines.FirstOrDefault()}...), " +
                   $"{nameof(OutLines)}: [{OutLines.Count}]({OutLines.FirstOrDefault()}...)";
        }
    }

    public class OneTimeScriptState : IDisposable
    {
        private static readonly IList<OneTimeScriptState> Instances = new List<OneTimeScriptState>();

        public string FileCreated { get; }
        public int LineNumberCreated { get; }
        private readonly ScriptState _scriptState;
        private bool _disposed = false;

        public ScriptState ScriptState
        {
            get
            {
                Dispose();
                return _scriptState;
            }
        }

        public OneTimeScriptState(ScriptState scriptState, string fileCreated, int lineNumberCreated = -1)
        {
            _scriptState = scriptState;
            FileCreated = fileCreated;
            LineNumberCreated = lineNumberCreated;
            Instances.Add(this);
        }

        public void Dispose()
        {
            Util.AssertWarning(
                !_disposed,
                $"{nameof(OneTimeScriptState)} has been disposed twice and likely represents mishandled execution state",
                FileCreated, LineNumberCreated);
            _disposed = true;
        }

        public static void CheckInstancesDisposed()
        {
            foreach (var instance in Instances)
            {
                Util.AssertWarning(instance._disposed,
                    $"{nameof(OneTimeScriptState)} is not disposed and likely represents lost execution state",
                    instance.FileCreated, instance.LineNumberCreated);
            }
        }

        public override string ToString()
        {
            return $"{nameof(FileCreated)}: {FileCreated}, {nameof(LineNumberCreated)}: {LineNumberCreated}";
        }
    }
}
