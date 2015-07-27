using Microsoft.CSharp;
using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;


namespace csrun
{
	class Program
	{
		static void Main(string[] args)
		{
			Params param = new Params(args);
			Assembly asm = compileSource(param);
			if (asm != null)
				execute(asm, param);
		}

		private static void execute(Assembly asm, Params param)
		{
			var flags = BindingFlags.ExactBinding | BindingFlags.InvokeMethod | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly;
			List<MethodInfo> methods = new List<MethodInfo>();
			if (param.entryPointType != null)
			{
				var t = asm.GetType(param.entryPointType, false);
				if (t != null)
				{
					var method = t.GetMethod(param.entryPointMethod, flags, null, new Type[] { typeof(string[]) }, null);
					if (method != null)
						methods.Add(method);
				}
			}
			else
			{
				foreach (var t in asm.GetTypes())
				{
					var method = t.GetMethod("Main", flags, null, new Type[] { typeof(string[]) }, null);
					if (method != null)
						methods.Add(method);
				}
			}
			if (methods.Count != 1)
			{
				Console.Error.WriteLine("Entry point not found. Use -entry.");
				Environment.ExitCode = 1;
				return;
			}

			try
			{
				methods[0].Invoke(null, new object[] { param.scriptArgs });
			}
			catch (Exception e)
			{
				Console.Error.WriteLine(e.ToString());
				Environment.ExitCode = 1;
			}
		}

		private static Assembly compileSource(Params param)
		{
			var cp = new CompilerParameters();
			cp.ReferencedAssemblies.Add("mscorlib.dll");
			cp.ReferencedAssemblies.Add("System.dll");
			cp.ReferencedAssemblies.Add("System.Core.dll");
			cp.GenerateInMemory = true;
			cp.CompilerOptions = "/optimize";
			cp.WarningLevel = 3;

			Dictionary<string, SourceFileDesc> files = new Dictionary<string, SourceFileDesc>();

			try
			{
				{
					var file = Path.GetFullPath(param.sourceFile);
					var sfd = new SourceFileDesc(file, getIncludeResolver(file), getImportResolver(file));
					files.Add(file, sfd);
				}

				var l = loadIncludes(files, files.Values);
				while (l.Count > 0)
				{
					foreach (var f in l)
						files[f.sourceFilePath] = f;
					l = loadIncludes(files, l);
				}

				foreach (var i in files.Values.SelectMany(x => x.imports))
					cp.ReferencedAssemblies.Add(i);

				using (var csp = new CSharpCodeProvider())
				{
					var sources = files.Values.Select(x => x.filePath).ToArray();
					var res = csp.CompileAssemblyFromFile(cp, sources);
					if (res.Errors.HasErrors)
					{
						foreach (CompilerError e in res.Errors)
						{
							var fd = files.Values.First(x => x.filePath.ToLower() == e.FileName.ToLower());
							Console.Error.WriteLine(fd.sourceFilePath + "(" + (e.Line + fd.lineOffset) + "," + e.Column + ") : error " + e.ErrorNumber + ": " + e.ErrorText);
						}
						Environment.ExitCode = 1;
						return null;
					}
					return res.CompiledAssembly;
				}
			}
			catch (FileNotFoundException e)
			{
				Console.Error.WriteLine("File not found: " + e.FileName);
				Environment.ExitCode = 1;
			}
			finally
			{
				foreach (var f in files.Values)
					f.deleteTempFile();
			}
			return null;
		}

		private static List<SourceFileDesc> loadIncludes(Dictionary<string, SourceFileDesc> files, IEnumerable<SourceFileDesc> sfds)
		{
			List<SourceFileDesc> res = new List<SourceFileDesc>();
			foreach (var sfd in sfds)
			{
				foreach (var f in sfd.includes.Except(files.Keys))
					res.Add(new SourceFileDesc(f, getIncludeResolver(f), getImportResolver(f)));
			}
			return res.Distinct().ToList();
		}

		private static IncludeResolver getIncludeResolver(string baseFile)
		{
			return new IncludeResolver(baseFile);
		}

		private static ImportResolver getImportResolver(string baseFile)
		{
			return new ImportResolver(baseFile);
		}
	}

	class Params
	{
		public string sourceFile { get; private set; }
		public string entryPointType { get; private set; }
		public string entryPointMethod { get; private set; }
		public string[] scriptArgs { get; private set; }

		public Params(string[] args)
		{
			for (int i = 0; i < args.Length; i++)
			{
				var p = args[i];
				if (!p.StartsWith("-"))
				{
					sourceFile = p;
					scriptArgs = args.Skip(i + 1).ToArray();
					break;
				}
				else if (p.StartsWith("-entry="))
				{
					p = p.Substring(6);
					int idx = p.LastIndexOf('.');
					if (idx <= 0 || p.Length == idx + 1)
					{
						Console.Error.WriteLine("Invalid entry point");
						printUsage();
						Environment.Exit(1);
					}
					entryPointType = p.Substring(0, idx);
					entryPointMethod = p.Substring(idx + 1);
				}
			}
		}

		private void printUsage()
		{
			Console.WriteLine("Usage:");
			Console.WriteLine("csrun.exe -h\n\tdisplay help");
			Console.WriteLine("csrun.exe [options] <script file> <script args>");
			Console.WriteLine("\t-entry\t\t-entry=<entry point>");
		}
	}

	class SourceFileDesc
	{
		public int lineOffset { get; private set; }
		public List<string> imports { get; private set; }
		public List<string> includes { get; private set; }
		public string filePath { get; private set; }
		public string sourceFilePath { get; private set; }

		public SourceFileDesc(string filename, IncludeResolver incRes, ImportResolver impRes)
		{
			sourceFilePath = filename;
			imports = new List<string>();
			includes = new List<string>();

			using (var sr = new StreamReader(filename))
			{
				bool makeTempFile = false;
				string s = sr.ReadLine();
				if (s != null && s.StartsWith("::{"))
				{
					makeTempFile = true;
					do
					{
						lineOffset++;
						s = sr.ReadLine();
					} while (s != null && !s.StartsWith("}::"));
					s = sr.ReadLine();
					lineOffset++;
				}

				while (s != null)
				{
					while (s != null && s.TrimStart().Length == 0)
					{
						s = sr.ReadLine();
						lineOffset++;
					}
					if (s != null)
					{
						if (s.TrimStart().StartsWith("/*"))
						{
							int idx = -1;
							while (s != null && (idx = s.IndexOf("*/")) < 0)
							{
								s = sr.ReadLine();
								lineOffset++;
							}
							if (idx >= 0)
							{
								s = s.Substring(idx + 2);
								continue;
							}
						}
						else if (parseImport(s, "//#import", f => imports.Add(impRes.GetFullPath(f))) || parseImport(s, "//#include", f => includes.Add(incRes.GetFullPath(f))))
						{
							s = sr.ReadLine();
							lineOffset++;
						}
						else if (s.TrimStart().StartsWith("//"))
						{
							s = "";
							continue;
						}
						else
							break;
					}
				}

				if (makeTempFile)
				{
					filePath = Path.GetTempFileName();
					using (var tempFile = new StreamWriter(filePath, false))
					{
						while (s != null)
						{
							tempFile.WriteLine(s);
							s = sr.ReadLine();
						}
					}
				}
				else
				{
					lineOffset = 0;
					filePath = sourceFilePath;
				}
			}
		}

		public void deleteTempFile()
		{
			if (filePath != sourceFilePath)
				File.Delete(filePath);
		}

		private bool parseImport(string s, string tag, Action<string> fn)
		{
			if (s.StartsWith(tag) && s.Length > tag.Length && char.IsWhiteSpace(s[tag.Length]))
			{
				fn(s.Substring(tag.Length + 1).Trim());
				return true;
			}
			return false;
		}
	}

	class IncludeResolver
	{
		string baseDir;

		public IncludeResolver(string baseFile)
		{
			if (!Directory.Exists(baseFile) && File.Exists(baseFile))
				baseFile = Path.GetDirectoryName(baseFile);
			baseDir = baseFile;
		}

		public string GetFullPath(string taget)
		{
			string res = Path.Combine(baseDir, taget);
			return res;
		}
	}

	class ImportResolver
	{
		string baseDir;

		public ImportResolver(string baseFile)
		{
			if (!Directory.Exists(baseFile) && File.Exists(baseFile))
				baseFile = Path.GetDirectoryName(baseFile);
			baseDir = baseFile;
		}

		public string GetFullPath(string taget)
		{
			string res = Path.Combine(baseDir, taget);
			if (!File.Exists(res))
				res = taget;
			return res;
		}
	}
}
