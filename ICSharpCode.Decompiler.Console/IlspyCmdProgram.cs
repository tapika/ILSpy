using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Threading;

using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.CSharp.ProjectDecompiler;
using ICSharpCode.Decompiler.DebugInfo;
using ICSharpCode.Decompiler.Disassembler;
using ICSharpCode.Decompiler.Metadata;
using ICSharpCode.Decompiler.PdbProvider;
using ICSharpCode.Decompiler.TypeSystem;

using McMaster.Extensions.CommandLineUtils;
// ReSharper disable All

namespace ICSharpCode.Decompiler.Console
{
	[Command(Name = "ilspycmd", Description = "dotnet tool for decompiling .NET assemblies and generating portable PDBs")]
	[HelpOption("-h|--help")]
	public class ILSpyCmdProgram
	{
		public static int Main(string[] args) => CommandLineApplication.Execute<ILSpyCmdProgram>(args);

		[FileExists]
		[Argument(0, "Assembly file name", "Assembly to decompile")]
		public string InputAssemblyName { get; }

		[DirectoryExists]
		[Option("-o|--outputdir <directory>", "The output directory, if omitted decompiler output is written to standard out.", CommandOptionType.SingleValue)]
		public string OutputDirectory { get; }

		[Option("-p|--project", "Decompile assembly as compilable project. If outputdir is omitted - saved to assembly folder", CommandOptionType.NoValue)]
		public bool WholeProjectDecompile { get; set; }

		[Option("-c|-crc|--crc", "Calculate assembly internal checksum", CommandOptionType.NoValue)]
		public bool CalculateChecksum { get; set; }

		[Option("-crclog|--crclog", "Write log on checksum calculation", CommandOptionType.NoValue)]
		public bool ChecksumWriteLog { get; set; }

		[Option("-f|--file", "Decompile assembly into single file.", CommandOptionType.NoValue)]
		public bool DecompileToFile { get; }

		[Option("-t|--type <type-name>", "The fully qualified name of the type to decompile.", CommandOptionType.SingleValue)]
		public string TypeName { get; }

		[Option("-il|--ilcode", "Show IL code.", CommandOptionType.NoValue)]
		public bool ShowILCodeFlag { get; }

		[Option("--il-sequence-points", "Show IL with sequence points. Implies -il.", CommandOptionType.NoValue)]
		public bool ShowILSequencePointsFlag { get; }

		[Option("-genpdb", "Generate PDB.", CommandOptionType.NoValue)]
		public bool CreateDebugInfoFlag { get; }

		[FileExistsOrNull]
		[Option("-usepdb", "Use PDB.", CommandOptionType.SingleOrNoValue)]
		public (bool IsSet, string Value) InputPDBFile { get; }

		[Option("-l|--list <entity-type(s)>", "Lists all entities of the specified type(s). Valid types: c(lass), i(nterface), s(truct), d(elegate), e(num)", CommandOptionType.MultipleValue)]
		public string[] EntityTypes { get; } = new string[0];

		[Option("-v|--version", "Show version of ICSharpCode.Decompiler used.", CommandOptionType.NoValue)]
		public bool ShowVersion { get; }

		[Option("-lv|--languageversion <version>", "C# Language version: CSharp1, CSharp2, CSharp3, CSharp4, CSharp5, CSharp6, CSharp7_0, CSharp7_1, CSharp7_2, CSharp7_3, CSharp8_0 or Latest", CommandOptionType.SingleValue)]
		public LanguageVersion LanguageVersion { get; } = LanguageVersion.Latest;

		[DirectoryExists]
		[Option("-r|--referencepath <path>", "Path to a directory containing dependencies of the assembly that is being decompiled.", CommandOptionType.MultipleValue)]
		public string[] ReferencePaths { get; } = new string[0];

		[Option("--no-dead-code", "Remove dead code.", CommandOptionType.NoValue)]
		public bool RemoveDeadCode { get; }

		[Option("--no-dead-stores", "Remove dead stores.", CommandOptionType.NoValue)]
		public bool RemoveDeadStores { get; }

		private int OnExecute(CommandLineApplication app)
		{
			if (InputAssemblyName == null)
			{
				app.HelpTextGenerator.Generate(app, System.Console.Out);
				return 2;
			}

			TextWriter output = System.Console.Out;
			bool outputDirectorySpecified = !string.IsNullOrEmpty(OutputDirectory);

			try
			{
				if (!(DecompileToFile || ShowILCodeFlag || ShowILSequencePointsFlag || CreateDebugInfoFlag || ShowVersion || CalculateChecksum))
				{
					WholeProjectDecompile = true;
				}

				if (WholeProjectDecompile || CalculateChecksum)
				{
					return DecompileAsProject(InputAssemblyName, OutputDirectory);
				}
				else if (EntityTypes.Any())
				{
					var values = EntityTypes.SelectMany(v => v.Split(',', ';')).ToArray();
					HashSet<TypeKind> kinds = TypesParser.ParseSelection(values);
					if (outputDirectorySpecified)
					{
						string outputName = Path.GetFileNameWithoutExtension(InputAssemblyName);
						output = File.CreateText(Path.Combine(OutputDirectory, outputName) + ".list.txt");
					}

					return ListContent(InputAssemblyName, output, kinds);
				}
				else if (ShowILCodeFlag || ShowILSequencePointsFlag)
				{
					if (outputDirectorySpecified)
					{
						string outputName = Path.GetFileNameWithoutExtension(InputAssemblyName);
						output = File.CreateText(Path.Combine(OutputDirectory, outputName) + ".il");
					}

					return ShowIL(InputAssemblyName, output);
				}
				else if (CreateDebugInfoFlag)
				{
					string pdbFileName = null;
					if (outputDirectorySpecified)
					{
						string outputName = Path.GetFileNameWithoutExtension(InputAssemblyName);
						pdbFileName = Path.Combine(OutputDirectory, outputName) + ".pdb";
					}
					else
					{
						pdbFileName = Path.ChangeExtension(InputAssemblyName, ".pdb");
					}

					return GeneratePdbForAssembly(InputAssemblyName, pdbFileName, app);
				}
				else if (ShowVersion)
				{
					string vInfo = "ilspycmd: " + typeof(ILSpyCmdProgram).Assembly.GetName().Version.ToString() +
								   Environment.NewLine
								   + "ICSharpCode.Decompiler: " +
								   typeof(FullTypeName).Assembly.GetName().Version.ToString();
					output.WriteLine(vInfo);
				}
				else
				{
					if (outputDirectorySpecified)
					{
						string outputName = Path.GetFileNameWithoutExtension(InputAssemblyName);
						output = File.CreateText(Path.Combine(OutputDirectory,
							(string.IsNullOrEmpty(TypeName) ? outputName : TypeName) + ".decompiled.cs"));
					}

					return Decompile(InputAssemblyName, output, TypeName);
				}
			}
			catch (Exception ex)
			{
				app.Error.WriteLine(ex.ToString());
				return ProgramExitCodes.EX_SOFTWARE;
			}
			finally
			{
				output.Close();
			}

			return 0;
		}

		DecompilerSettings GetSettings()
		{
			return new DecompilerSettings(LanguageVersion) {
				ThrowOnAssemblyResolveErrors = false,
				RemoveDeadCode = RemoveDeadCode,
				RemoveDeadStores = RemoveDeadStores
			};
		}

		CSharpDecompiler GetDecompiler(string assemblyFileName)
		{
			var module = new PEFile(assemblyFileName);
			var resolver = new UniversalAssemblyResolver(assemblyFileName, false, module.Reader.DetectTargetFrameworkId());
			foreach (var path in ReferencePaths)
			{
				resolver.AddSearchDirectory(path);
			}
			return new CSharpDecompiler(assemblyFileName, resolver, GetSettings()) {
				DebugInfoProvider = TryLoadPDB(module)
			};
		}

		int ListContent(string assemblyFileName, TextWriter output, ISet<TypeKind> kinds)
		{
			CSharpDecompiler decompiler = GetDecompiler(assemblyFileName);

			foreach (var type in decompiler.TypeSystem.MainModule.TypeDefinitions)
			{
				if (!kinds.Contains(type.Kind))
					continue;
				output.WriteLine($"{type.Kind} {type.FullName}");
			}
			return 0;
		}

		int ShowIL(string assemblyFileName, TextWriter output)
		{
			var module = new PEFile(assemblyFileName);
			output.WriteLine($"// IL code: {module.Name}");
			var disassembler = new ReflectionDisassembler(new PlainTextOutput(output), CancellationToken.None) {
				DebugInfo = TryLoadPDB(module),
				ShowSequencePoints = ShowILSequencePointsFlag,
			};
			disassembler.WriteModuleContents(module);
			return 0;
		}

		int DecompileAsProject(string assemblyFileName, string outputDirectory)
		{
			string path = Path.GetFullPath(assemblyFileName);
			if (outputDirectory == null)
			{
				string inputDir = Path.GetDirectoryName(path);
				string relFile = Path.GetFileNameWithoutExtension(path);
				outputDirectory = Path.Combine(inputDir, relFile);
			}

			if (WholeProjectDecompile)
			{
				if (!Directory.Exists(outputDirectory))
				{
					Directory.CreateDirectory(outputDirectory);
				}
			}

			if (WholeProjectDecompile)
			{
				string file = Path.GetFileName(path);
				System.Console.Write($"Decompiling {file}... ");
			}

			Stopwatch w = Stopwatch.StartNew();

			var module = new PEFile(assemblyFileName);
			var resolver = new UniversalAssemblyResolver(assemblyFileName, false, module.Reader.DetectTargetFrameworkId());
			foreach (var refpath in ReferencePaths)
			{
				resolver.AddSearchDirectory(refpath);
			}

			var settings = GetSettings();
			if (CalculateChecksum)
			{
				settings.checksumCalc.EnableChecksumCalculation(HashAlgorithmName.SHA256);
			}

			if (ChecksumWriteLog)
			{ 
				settings.checksumCalc.EnableChecksumLog(assemblyFileName);
			}

			settings.ProduceSourceCode = WholeProjectDecompile;

			var decompiler = new WholeProjectDecompiler(settings, resolver, resolver, TryLoadPDB(module));
			decompiler.DecompileProject(module, outputDirectory);

			if (WholeProjectDecompile)
			{
				System.Console.WriteLine($"ok.");
				System.Console.WriteLine($"Used time: {w.Elapsed.TotalSeconds:f2} sec");
			}

			if (CalculateChecksum)
			{
				System.Console.WriteLine($"'{Path.GetFileName(assemblyFileName)}' checksum:");
				System.Console.WriteLine($"    '{settings.checksumCalc.GetHashString()}'");
			}
			
			if (ChecksumWriteLog)
			{
				settings.checksumCalc.Dispose();
			}

			return 0;
		}

		int Decompile(string assemblyFileName, TextWriter output, string typeName = null)
		{
			CSharpDecompiler decompiler = GetDecompiler(assemblyFileName);

			if (typeName == null)
			{
				output.Write(decompiler.DecompileWholeModuleAsString());
			}
			else
			{
				var name = new FullTypeName(typeName);
				output.Write(decompiler.DecompileTypeAsString(name));
			}
			return 0;
		}

		int GeneratePdbForAssembly(string assemblyFileName, string pdbFileName, CommandLineApplication app)
		{
			var module = new PEFile(assemblyFileName,
				new FileStream(assemblyFileName, FileMode.Open, FileAccess.Read),
				PEStreamOptions.PrefetchEntireImage,
				metadataOptions: MetadataReaderOptions.None);

			if (!PortablePdbWriter.HasCodeViewDebugDirectoryEntry(module))
			{
				app.Error.WriteLine($"Cannot create PDB file for {assemblyFileName}, because it does not contain a PE Debug Directory Entry of type 'CodeView'.");
				return ProgramExitCodes.EX_DATAERR;
			}

			using (FileStream stream = new FileStream(pdbFileName, FileMode.OpenOrCreate, FileAccess.Write))
			{
				var decompiler = GetDecompiler(assemblyFileName);
				PortablePdbWriter.WritePdb(module, decompiler, GetSettings(), stream);
			}

			return 0;
		}

		IDebugInfoProvider TryLoadPDB(PEFile module)
		{
			if (InputPDBFile.IsSet)
			{
				if (InputPDBFile.Value == null)
					return DebugInfoUtils.LoadSymbols(module);
				return DebugInfoUtils.FromFile(module, InputPDBFile.Value);
			}

			return null;
		}
	}
}
