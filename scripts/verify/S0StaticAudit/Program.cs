using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace S0StaticAudit;

internal static class Program
{
    private const string DefaultManagedDir = @"D:\SteamLibrary\steamapps\common\Hollow Knight\hollow_knight_Data\Managed";

    private static readonly HashSet<string> LifecycleMethodNames = new(StringComparer.Ordinal)
    {
        "Awake",
        "OnEnable",
        "Start",
        "Update",
        "FixedUpdate",
        "LateUpdate",
        "OnDisable",
        "OnDestroy"
    };

    private static readonly HashSet<string> LeanS0CoveredRngTypes = new(StringComparer.Ordinal)
    {
        "RandomRotationRange",
        "RandomRotation",
        "RandomScale",
        "AudioSourcePitchRandomizer",
        "RandomAudioStart",
        "FastNoise",
        "PlayFromRandomFrame",
        "PlayFromRandomFrameMecanim",
        "BasicSpriteAnimator",
        "SetZRandom",
        "SetZ",
        "SpinSelf",
        "SpinSelfSimple",
        "CharmDisplay",
        "InvAnimateUpAndDown"
    };

    private static readonly HashSet<string> LeanS0CoveredStaticFields = new(StringComparer.Ordinal)
    {
        "BossSequenceController.currentData",
        "BossSequenceController.currentSequence",
        "BossSequenceController.bossIndex",
        "BossSequenceController.<WasCompleted>k__BackingField",
        "BossChallengeUI.lastSelectedButton",
        "BossSceneController.SetupEvent",
        "StaticVariableList.variables",
        "iTween.tweens",
        "TimeController.slowMotionTimeScale",
        "TimeController.pauseTimeScale",
        "TimeController.platformBackgroundTimeScale",
        "TimeController.genericTimeScale",
        "BounceShroom.nextBounceParticleTime",
        "BounceShroom.nextCamShakeTime",
        "BounceShroom.heroParticles",
        "DreamPlantOrb.pitchReturnTime",
        "DreamPlantOrb.currentPitch",
        "HutongGames.PlayMaker.Actions.SetEventProperties.properties",
        "LimitBehaviour.behaviourLists",
        "SceneAdditiveLoadConditional.additiveSceneLoads",
        "SceneAdditiveLoadConditional.loadInSequence",
        "ScenePreloader.pendingOperations",
        "ScenePreloader.completedOperations"
    };

    private static readonly HashSet<string> LeanS0CoveredComponents = new(StringComparer.Ordinal)
    {
        "RandomRotationRange",
        "RandomRotation",
        "RandomScale",
        "AudioSourcePitchRandomizer",
        "RandomAudioStart",
        "DeactivateAfterDelay",
        "AutoRecycleSelf",
        "ObjectPool",
        "PersonalObjectPool",
        "ObjectBounce",
        "HeroController",
        "CameraController",
        "UIManager",
        "GameManager",
        "GameCameras",
        "AudioManager",
        "FastNoise",
        "PlayFromRandomFrame",
        "PlayFromRandomFrameMecanim",
        "BasicSpriteAnimator",
        "SetZRandom",
        "SetZ",
        "SpinSelf",
        "SpinSelfSimple",
        "CharmDisplay",
        "InvAnimateUpAndDown",
        "HeroAudioController"
    };

    private static readonly string[] TimerOrCounterKeywords =
    [
        "timer", "time", "delay", "cooldown", "duration", "elapsed",
        "count", "counter", "index", "frame", "tick", "step", "seed"
    ];

    private static readonly string[] NameRiskKeywords =
    [
        "Random", "Delay", "Bounce", "Shake", "Recycle", "Pool"
    ];

    private static readonly HashSet<string> NumericTypeFullNames = new(StringComparer.Ordinal)
    {
        "System.Int32", "System.UInt32", "System.Int64", "System.UInt64",
        "System.Single", "System.Double", "System.Int16", "System.UInt16",
        "System.Byte", "System.SByte", "System.Decimal"
    };

    private static readonly HashSet<string> PrimitiveValueFullNames = new(StringComparer.Ordinal)
    {
        "System.Boolean", "System.Char", "System.String", "System.IntPtr", "System.UIntPtr",
        "System.Int32", "System.UInt32", "System.Int64", "System.UInt64",
        "System.Single", "System.Double", "System.Int16", "System.UInt16",
        "System.Byte", "System.SByte", "System.Decimal"
    };

    public static int Main(string[] args)
    {
        if (!TryParseArgs(args, out string managedDir, out string outDir, out string? error))
        {
            Console.Error.WriteLine(error);
            PrintUsage();
            return 1;
        }

        if (!Directory.Exists(managedDir))
        {
            Console.Error.WriteLine($"Managed directory not found: {managedDir}");
            return 1;
        }

        string mainDllPath = Path.Combine(managedDir, "Assembly-CSharp.dll");
        if (!File.Exists(mainDllPath))
        {
            Console.Error.WriteLine($"Required assembly not found: {mainDllPath}");
            return 1;
        }

        Directory.CreateDirectory(outDir);

        using var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(managedDir);
        var readerParams = new ReaderParameters
        {
            AssemblyResolver = resolver,
            ReadSymbols = false
        };

        var targetAssemblies = new (string FileName, bool Required)[]
        {
            ("Assembly-CSharp.dll", true),
            ("Assembly-CSharp-firstpass.dll", false),
            ("PlayMaker.dll", false)
        };

        var scannedAssemblies = new List<string>();
        var rngCallers = new List<RngCallerRecord>();
        var mutableStaticFields = new List<MutableStaticFieldRecord>();
        var ddolCalls = new List<DdolCallRecord>();
        var componentRisks = new List<ComponentRiskRecord>();
        int totalMonoBehaviours = 0;

        foreach ((string fileName, bool required) in targetAssemblies)
        {
            string dllPath = Path.Combine(managedDir, fileName);
            if (!File.Exists(dllPath))
            {
                if (required)
                {
                    Console.Error.WriteLine($"Required assembly missing: {dllPath}");
                    return 1;
                }

                continue;
            }

            using AssemblyDefinition assembly = AssemblyDefinition.ReadAssembly(dllPath, readerParams);
            scannedAssemblies.Add(fileName);

            List<TypeDefinition> allTypes = FlattenTypes(assembly.MainModule.Types);
            ScanRngCallers(fileName, allTypes, rngCallers);

            if (string.Equals(fileName, "Assembly-CSharp.dll", StringComparison.OrdinalIgnoreCase))
            {
                ScanMutableStaticFields(allTypes, mutableStaticFields);
                ScanDdolCalls(allTypes, ddolCalls);
                totalMonoBehaviours = ScanComponentRisks(allTypes, componentRisks);
            }
        }

        rngCallers.Sort((a, b) =>
        {
            int cmp = StringComparer.Ordinal.Compare(a.Assembly, b.Assembly);
            if (cmp != 0) return cmp;
            cmp = StringComparer.Ordinal.Compare(a.TypeName, b.TypeName);
            if (cmp != 0) return cmp;
            cmp = StringComparer.Ordinal.Compare(a.MethodName, b.MethodName);
            return cmp != 0 ? cmp : StringComparer.Ordinal.Compare(a.RandomTarget, b.RandomTarget);
        });

        mutableStaticFields.Sort((a, b) =>
        {
            int cmp = StringComparer.Ordinal.Compare(a.Category, b.Category);
            if (cmp != 0) return cmp;
            cmp = StringComparer.Ordinal.Compare(a.TypeName, b.TypeName);
            return cmp != 0 ? cmp : StringComparer.Ordinal.Compare(a.FieldName, b.FieldName);
        });

        ddolCalls.Sort((a, b) =>
        {
            int cmp = StringComparer.Ordinal.Compare(a.TypeName, b.TypeName);
            return cmp != 0 ? cmp : StringComparer.Ordinal.Compare(a.MethodName, b.MethodName);
        });

        componentRisks.Sort((a, b) =>
        {
            int cmp = b.RiskTags.Count.CompareTo(a.RiskTags.Count);
            if (cmp != 0) return cmp;
            cmp = a.CoveredByLeanS0.CompareTo(b.CoveredByLeanS0);
            return cmp != 0 ? cmp : StringComparer.Ordinal.Compare(a.TypeName, b.TypeName);
        });

        AuditSummary summary = BuildSummary(
            scannedAssemblies,
            totalMonoBehaviours,
            rngCallers,
            mutableStaticFields,
            ddolCalls,
            componentRisks);

        var report = new StaticAuditReport
        {
            GeneratedAtUtc = DateTimeOffset.UtcNow.ToString("O"),
            ManagedDir = Path.GetFullPath(managedDir),
            ScannedAssemblies = scannedAssemblies,
            Summary = summary,
            RngCallers = rngCallers,
            MutableStaticFields = mutableStaticFields,
            DdolCalls = ddolCalls,
            ComponentRisks = componentRisks
        };

        string jsonPath = Path.Combine(outDir, "s0-static-audit.json");
        string mdPath = Path.Combine(outDir, "s0-static-audit.md");

        var jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never
        };
        File.WriteAllText(jsonPath, JsonSerializer.Serialize(report, jsonOptions), new UTF8Encoding(false));
        File.WriteAllText(mdPath, RenderMarkdownReport(report), new UTF8Encoding(false));

        Console.WriteLine($"JSON report: {jsonPath}");
        Console.WriteLine($"Markdown report: {mdPath}");
        Console.WriteLine(
            $"Summary: rngCallers={summary.RngTotal} (covered={summary.RngCovered}, uncovered={summary.RngUncovered}), " +
            $"mutableStatic={summary.StaticTotal} (covered={summary.StaticCovered}, uncovered={summary.StaticUncovered}), " +
            $"ddolCalls={summary.DdolTotal} (covered={summary.DdolCovered}, uncovered={summary.DdolUncovered}), " +
            $"componentRisks={summary.ComponentRiskTotal}/{summary.TotalMonoBehaviours} (covered={summary.ComponentRiskCovered}, uncovered={summary.ComponentRiskUncovered})");

        return 0;
    }

    private static bool TryParseArgs(string[] args, out string managedDir, out string outDir, out string? error)
    {
        managedDir = DefaultManagedDir;
        outDir = Directory.GetCurrentDirectory();
        error = null;

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (string.Equals(arg, "--managed-dir", StringComparison.Ordinal))
            {
                if (i + 1 >= args.Length)
                {
                    error = "Missing value for --managed-dir";
                    return false;
                }

                managedDir = args[++i];
            }
            else if (string.Equals(arg, "--out-dir", StringComparison.Ordinal))
            {
                if (i + 1 >= args.Length)
                {
                    error = "Missing value for --out-dir";
                    return false;
                }

                outDir = args[++i];
            }
            else if (string.Equals(arg, "-h", StringComparison.Ordinal) || string.Equals(arg, "--help", StringComparison.Ordinal))
            {
                error = "Usage requested.";
                return false;
            }
            else
            {
                error = $"Unknown argument: {arg}";
                return false;
            }
        }

        return true;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Usage: S0StaticAudit [--managed-dir <path>] [--out-dir <path>]");
    }

    private static List<TypeDefinition> FlattenTypes(IEnumerable<TypeDefinition> rootTypes)
    {
        var result = new List<TypeDefinition>();
        var stack = new Stack<TypeDefinition>();
        foreach (TypeDefinition t in rootTypes)
        {
            stack.Push(t);
        }

        while (stack.Count > 0)
        {
            TypeDefinition current = stack.Pop();
            if (string.Equals(current.Name, "<Module>", StringComparison.Ordinal))
            {
                continue;
            }

            result.Add(current);
            if (current.HasNestedTypes)
            {
                foreach (TypeDefinition nested in current.NestedTypes)
                {
                    stack.Push(nested);
                }
            }
        }

        return result;
    }

    private static void ScanRngCallers(string assemblyName, List<TypeDefinition> allTypes, List<RngCallerRecord> output)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (TypeDefinition type in allTypes)
        {
            if (!type.HasMethods)
            {
                continue;
            }

            bool isMono = IsMonoBehaviourOrEnclosed(type);
            bool isAction = IsPlayMakerActionOrEnclosed(type);
            bool covered = MatchesAnyTypeName(type, LeanS0CoveredRngTypes);

            foreach (MethodDefinition method in type.Methods)
            {
                if (!method.HasBody || method.Body == null)
                {
                    continue;
                }

                bool isLifecycle = LifecycleMethodNames.Contains(method.Name);

                foreach (Instruction instr in method.Body.Instructions)
                {
                    if (!IsCallInstruction(instr.OpCode) || instr.Operand is not MethodReference targetMethod)
                    {
                        continue;
                    }

                    string? declaringFullName = targetMethod.DeclaringType?.FullName;
                    bool isUnityRandom = string.Equals(declaringFullName, "UnityEngine.Random", StringComparison.Ordinal)
                                         && !string.Equals(targetMethod.Name, "get_state", StringComparison.Ordinal);
                    bool isSystemRandom = string.Equals(declaringFullName, "System.Random", StringComparison.Ordinal);

                    if (!isUnityRandom && !isSystemRandom)
                    {
                        continue;
                    }

                    string randomTarget = $"{declaringFullName}::{targetMethod.Name}";
                    string dedupKey = $"{assemblyName}|{type.FullName}|{method.Name}|{randomTarget}";
                    if (!seen.Add(dedupKey))
                    {
                        continue;
                    }

                    output.Add(new RngCallerRecord
                    {
                        Assembly = assemblyName,
                        TypeName = type.FullName,
                        MethodName = method.Name,
                        RandomTarget = randomTarget,
                        IsUnityRandom = isUnityRandom,
                        IsSystemRandom = isSystemRandom,
                        IsMonoBehaviour = isMono,
                        IsPlayMakerAction = isAction,
                        IsLifecycleMethod = isLifecycle,
                        CoveredByLeanS0 = covered
                    });
                }
            }
        }
    }

    private static void ScanMutableStaticFields(List<TypeDefinition> allTypes, List<MutableStaticFieldRecord> output)
    {
        foreach (TypeDefinition type in allTypes)
        {
            if (IsCompilerStaticNoiseType(type) || !type.HasFields)
            {
                continue;
            }

            foreach (FieldDefinition field in type.Fields)
            {
                if (!field.IsStatic || field.IsLiteral || field.IsInitOnly)
                {
                    continue;
                }

                if (IsStaticArrayNoiseField(field))
                {
                    continue;
                }

                string category = ClassifyStaticField(field);
                string shortKey = $"{StripGenericArity(type.Name)}.{field.Name}";
                string fullKey = $"{StripGenericArity(type.FullName)}.{field.Name}";
                bool covered = LeanS0CoveredStaticFields.Contains(shortKey) || LeanS0CoveredStaticFields.Contains(fullKey);

                output.Add(new MutableStaticFieldRecord
                {
                    TypeName = type.FullName,
                    FieldName = field.Name,
                    FieldType = field.FieldType.FullName,
                    Category = category,
                    CoveredByLeanS0 = covered
                });
            }
        }
    }

    private static void ScanDdolCalls(List<TypeDefinition> allTypes, List<DdolCallRecord> output)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (TypeDefinition type in allTypes)
        {
            if (!type.HasMethods)
            {
                continue;
            }

            bool isMono = IsMonoBehaviourOrEnclosed(type);
            bool covered = MatchesAnyTypeName(type, LeanS0CoveredComponents);

            foreach (MethodDefinition method in type.Methods)
            {
                if (!method.HasBody || method.Body == null)
                {
                    continue;
                }

                bool isLifecycle = LifecycleMethodNames.Contains(method.Name);

                foreach (Instruction instr in method.Body.Instructions)
                {
                    if (!IsCallInstruction(instr.OpCode) || instr.Operand is not MethodReference targetMethod)
                    {
                        continue;
                    }

                    if (!string.Equals(targetMethod.DeclaringType?.FullName, "UnityEngine.Object", StringComparison.Ordinal)
                        || !string.Equals(targetMethod.Name, "DontDestroyOnLoad", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    string key = $"{type.FullName}|{method.Name}";
                    if (!seen.Add(key))
                    {
                        continue;
                    }

                    output.Add(new DdolCallRecord
                    {
                        TypeName = type.FullName,
                        MethodName = method.Name,
                        IsMonoBehaviour = isMono,
                        IsLifecycleMethod = isLifecycle,
                        CoveredByLeanS0 = covered
                    });
                }
            }
        }
    }

    private static int ScanComponentRisks(List<TypeDefinition> allTypes, List<ComponentRiskRecord> output)
    {
        var compilerNestedByOwner = new Dictionary<TypeDefinition, List<TypeDefinition>>();
        var monoBehaviours = new List<TypeDefinition>();

        foreach (TypeDefinition type in allTypes)
        {
            if (IsCompilerGeneratedNestedType(type))
            {
                TypeDefinition? owner = FindNearestNonCompilerEnclosingType(type);
                if (owner != null)
                {
                    if (!compilerNestedByOwner.TryGetValue(owner, out List<TypeDefinition>? list))
                    {
                        list = new List<TypeDefinition>();
                        compilerNestedByOwner[owner] = list;
                    }

                    list.Add(type);
                }

                continue;
            }

            if (InheritsFrom(type, "UnityEngine.MonoBehaviour"))
            {
                monoBehaviours.Add(type);
            }
        }

        foreach (TypeDefinition mb in monoBehaviours)
        {
            var typesToInspect = new List<TypeDefinition> { mb };
            if (compilerNestedByOwner.TryGetValue(mb, out List<TypeDefinition>? nestedList))
            {
                typesToInspect.AddRange(nestedList);
            }

            var tags = new List<string>();
            bool callsUnityRandom = false;
            bool callsSystemRandom = false;
            bool callsDdol = false;
            bool usesObjectPool = false;
            bool usesPersonalObjectPool = false;
            bool usesITween = IsOrInherits(mb, "iTween") || IsOrInherits(mb, "iTweenFSMEvents");
            bool hasAutoRecycle = IsOrInherits(mb, "AutoRecycleSelf") || IsOrInherits(mb, "DeactivateAfterDelay");

            foreach (TypeDefinition inspectType in typesToInspect)
            {
                if (inspectType.HasFields)
                {
                    foreach (FieldDefinition field in inspectType.Fields)
                    {
                        string ft = field.FieldType.FullName;
                        if (ContainsTypeToken(ft, "ObjectPool") && !ContainsTypeToken(ft, "PersonalObjectPool"))
                        {
                            usesObjectPool = true;
                        }

                        if (ContainsTypeToken(ft, "PersonalObjectPool"))
                        {
                            usesPersonalObjectPool = true;
                        }

                        if (ContainsTypeToken(ft, "iTween") || ContainsTypeToken(ft, "iTweenFSMEvents"))
                        {
                            usesITween = true;
                        }

                        if (ContainsTypeToken(ft, "AutoRecycleSelf") || ContainsTypeToken(ft, "DeactivateAfterDelay"))
                        {
                            hasAutoRecycle = true;
                        }
                    }
                }

                if (!inspectType.HasMethods)
                {
                    continue;
                }

                foreach (MethodDefinition method in inspectType.Methods)
                {
                    if (!method.HasBody || method.Body == null)
                    {
                        continue;
                    }

                    foreach (Instruction instr in method.Body.Instructions)
                    {
                        if (!IsCallInstruction(instr.OpCode) || instr.Operand is not MethodReference target)
                        {
                            continue;
                        }

                        string? decl = target.DeclaringType?.FullName;
                        if (string.IsNullOrEmpty(decl))
                        {
                            continue;
                        }

                        if (string.Equals(decl, "UnityEngine.Random", StringComparison.Ordinal)
                            && !string.Equals(target.Name, "get_state", StringComparison.Ordinal))
                        {
                            callsUnityRandom = true;
                        }
                        else if (string.Equals(decl, "System.Random", StringComparison.Ordinal))
                        {
                            callsSystemRandom = true;
                        }
                        else if (string.Equals(decl, "UnityEngine.Object", StringComparison.Ordinal)
                                 && string.Equals(target.Name, "DontDestroyOnLoad", StringComparison.Ordinal))
                        {
                            callsDdol = true;
                        }

                        if (ContainsTypeToken(decl, "PersonalObjectPool"))
                        {
                            usesPersonalObjectPool = true;
                        }
                        else if (ContainsTypeToken(decl, "ObjectPool"))
                        {
                            usesObjectPool = true;
                        }

                        if (ContainsTypeToken(decl, "iTween") || ContainsTypeToken(decl, "iTweenFSMEvents"))
                        {
                            usesITween = true;
                        }

                        if (ContainsTypeToken(decl, "AutoRecycleSelf") || ContainsTypeToken(decl, "DeactivateAfterDelay"))
                        {
                            hasAutoRecycle = true;
                        }
                    }
                }
            }

            if (callsUnityRandom) tags.Add("CallsUnityRandom");
            if (callsSystemRandom) tags.Add("CallsSystemRandom");
            if (callsDdol) tags.Add("CallsDontDestroyOnLoad");
            if (usesObjectPool) tags.Add("UsesObjectPool");
            if (usesPersonalObjectPool) tags.Add("UsesPersonalObjectPool");
            if (usesITween) tags.Add("UsesITween");
            if (hasAutoRecycle) tags.Add("HasAutoRecycle");

            string simpleName = StripGenericArity(mb.Name);
            if (NameRiskKeywords.Any(k => simpleName.Contains(k, StringComparison.OrdinalIgnoreCase)))
            {
                tags.Add("NameLooksRandomOrDelay");
            }

            if (tags.Count == 0)
            {
                continue;
            }

            output.Add(new ComponentRiskRecord
            {
                TypeName = mb.FullName,
                SimpleName = simpleName,
                RiskTags = tags,
                CoveredByLeanS0 = LeanS0CoveredComponents.Contains(simpleName)
            });
        }

        return monoBehaviours.Count;
    }

    private static bool IsCallInstruction(OpCode opCode)
        => opCode == OpCodes.Call || opCode == OpCodes.Callvirt || opCode == OpCodes.Newobj;

    private static bool IsCompilerStaticNoiseType(TypeDefinition type)
    {
        if (string.Equals(type.Name, "<>c", StringComparison.Ordinal)
            || type.Name.StartsWith("<>c__", StringComparison.Ordinal)
            || type.FullName.Contains("/<>c", StringComparison.Ordinal))
        {
            return true;
        }

        return type.FullName.StartsWith("<PrivateImplementationDetails>", StringComparison.Ordinal)
               || type.Name.Contains("__StaticArrayInitTypeSize", StringComparison.Ordinal);
    }

    private static bool IsStaticArrayNoiseField(FieldDefinition field)
    {
        return field.Name.Contains("__StaticArrayInitTypeSize", StringComparison.Ordinal)
               || field.FieldType.FullName.Contains("__StaticArrayInitTypeSize", StringComparison.Ordinal);
    }

    private static string ClassifyStaticField(FieldDefinition field)
    {
        TypeReference fieldType = field.FieldType;
        string fullName = fieldType.FullName;

        if (string.Equals(fullName, "System.Random", StringComparison.Ordinal)
            || string.Equals(fullName, "UnityEngine.Random", StringComparison.Ordinal)
            || string.Equals(fullName, "UnityEngine.Random/State", StringComparison.Ordinal)
            || ContainsTypeToken(fullName, "Random")
            || field.Name.Contains("Random", StringComparison.OrdinalIgnoreCase))
        {
            return "StaticRandom";
        }

        TypeDefinition? resolved = TryResolve(fieldType);
        if (IsDelegateType(fieldType, resolved))
        {
            return "StaticDelegateOrEvent";
        }

        if (IsCollectionType(fieldType, resolved))
        {
            return "StaticCollection";
        }

        if (NumericTypeFullNames.Contains(fullName)
            && TimerOrCounterKeywords.Any(k => field.Name.Contains(k, StringComparison.OrdinalIgnoreCase)))
        {
            return "StaticTimerOrCounter";
        }

        if (PrimitiveValueFullNames.Contains(fullName) || fieldType.IsValueType || (resolved != null && resolved.IsValueType))
        {
            return "StaticValue";
        }

        return "StaticSingletonOrRef";
    }

    private static bool IsDelegateType(TypeReference typeRef, TypeDefinition? resolved)
    {
        if (string.Equals(typeRef.FullName, "System.Delegate", StringComparison.Ordinal)
            || string.Equals(typeRef.FullName, "System.MulticastDelegate", StringComparison.Ordinal))
        {
            return true;
        }

        return resolved != null && (InheritsFrom(resolved, "System.Delegate") || InheritsFrom(resolved, "System.MulticastDelegate"));
    }

    private static bool IsCollectionType(TypeReference typeRef, TypeDefinition? resolved)
    {
        if (typeRef.IsArray)
        {
            return true;
        }

        string elementName = typeRef.GetElementType().FullName;
        if (elementName.StartsWith("System.Collections.", StringComparison.Ordinal)
            && !elementName.Contains("Enumerator", StringComparison.Ordinal)
            && !elementName.Contains("KeyValuePair", StringComparison.Ordinal))
        {
            return true;
        }

        if (resolved == null)
        {
            return false;
        }

        return ImplementsInterface(resolved, "System.Collections.ICollection")
               || ImplementsInterface(resolved, "System.Collections.IDictionary")
               || ImplementsInterface(resolved, "System.Collections.IList")
               || ImplementsInterfacePrefix(resolved, "System.Collections.Generic.ICollection`1")
               || ImplementsInterfacePrefix(resolved, "System.Collections.Generic.IDictionary`2");
    }

    private static bool ImplementsInterface(TypeDefinition type, string interfaceFullName)
    {
        TypeDefinition? current = type;
        int guard = 0;
        while (current != null && guard++ < 32)
        {
            if (current.HasInterfaces)
            {
                foreach (InterfaceImplementation iface in current.Interfaces)
                {
                    if (string.Equals(iface.InterfaceType.FullName, interfaceFullName, StringComparison.Ordinal))
                    {
                        return true;
                    }
                }
            }

            current = TryResolve(current.BaseType);
        }

        return false;
    }

    private static bool ImplementsInterfacePrefix(TypeDefinition type, string interfacePrefix)
    {
        TypeDefinition? current = type;
        int guard = 0;
        while (current != null && guard++ < 32)
        {
            if (current.HasInterfaces)
            {
                foreach (InterfaceImplementation iface in current.Interfaces)
                {
                    if (iface.InterfaceType.FullName.StartsWith(interfacePrefix, StringComparison.Ordinal))
                    {
                        return true;
                    }
                }
            }

            current = TryResolve(current.BaseType);
        }

        return false;
    }

    private static bool IsMonoBehaviourOrEnclosed(TypeDefinition type)
    {
        if (InheritsFrom(type, "UnityEngine.MonoBehaviour"))
        {
            return true;
        }

        if (!IsCompilerGeneratedNestedType(type))
        {
            return false;
        }

        foreach (TypeDefinition enclosing in GetEnclosingTypes(type))
        {
            if (InheritsFrom(enclosing, "UnityEngine.MonoBehaviour"))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsPlayMakerActionOrEnclosed(TypeDefinition type)
    {
        if (InheritsFrom(type, "HutongGames.PlayMaker.FsmStateAction"))
        {
            return true;
        }

        if (!IsCompilerGeneratedNestedType(type))
        {
            return false;
        }

        foreach (TypeDefinition enclosing in GetEnclosingTypes(type))
        {
            if (InheritsFrom(enclosing, "HutongGames.PlayMaker.FsmStateAction"))
            {
                return true;
            }
        }

        return false;
    }

    private static bool MatchesAnyTypeName(TypeDefinition type, HashSet<string> targetSimpleNames)
    {
        if (targetSimpleNames.Contains(StripGenericArity(type.Name)))
        {
            return true;
        }

        foreach (TypeDefinition enclosing in GetEnclosingTypes(type))
        {
            if (targetSimpleNames.Contains(StripGenericArity(enclosing.Name)))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsCompilerGeneratedNestedType(TypeDefinition type)
    {
        if (!type.IsNested)
        {
            return false;
        }

        return type.Name.StartsWith("<", StringComparison.Ordinal)
               || type.Name.Contains("DisplayClass", StringComparison.Ordinal)
               || (type.HasCustomAttributes && type.CustomAttributes.Any(a =>
                   string.Equals(a.AttributeType.FullName, "System.Runtime.CompilerServices.CompilerGeneratedAttribute", StringComparison.Ordinal)));
    }

    private static TypeDefinition? FindNearestNonCompilerEnclosingType(TypeDefinition type)
    {
        TypeDefinition? current = type.DeclaringType;
        while (current != null)
        {
            if (!IsCompilerGeneratedNestedType(current))
            {
                return current;
            }

            current = current.DeclaringType;
        }

        return null;
    }

    private static IEnumerable<TypeDefinition> GetEnclosingTypes(TypeDefinition type)
    {
        TypeDefinition? current = type.DeclaringType;
        while (current != null)
        {
            yield return current;
            current = current.DeclaringType;
        }
    }

    private static bool IsOrInherits(TypeDefinition type, string simpleOrFullName)
    {
        TypeDefinition? current = type;
        int guard = 0;
        while (current != null && guard++ < 32)
        {
            if (string.Equals(current.FullName, simpleOrFullName, StringComparison.Ordinal)
                || string.Equals(StripGenericArity(current.Name), simpleOrFullName, StringComparison.Ordinal))
            {
                return true;
            }

            current = TryResolve(current.BaseType);
        }

        return false;
    }

    private static bool InheritsFrom(TypeDefinition? type, string baseFullName)
    {
        TypeDefinition? current = type;
        int guard = 0;
        while (current != null && guard++ < 32)
        {
            if (string.Equals(current.FullName, baseFullName, StringComparison.Ordinal))
            {
                return true;
            }

            TypeReference? baseRef = current.BaseType;
            if (baseRef == null)
            {
                return false;
            }

            if (string.Equals(baseRef.FullName, baseFullName, StringComparison.Ordinal))
            {
                return true;
            }

            current = TryResolve(baseRef);
        }

        return false;
    }

    private static TypeDefinition? TryResolve(TypeReference? typeRef)
    {
        if (typeRef == null)
        {
            return null;
        }

        try
        {
            return typeRef.Resolve();
        }
        catch
        {
            return null;
        }
    }

    private static string StripGenericArity(string name)
    {
        int idx = name.IndexOf('`');
        return idx >= 0 ? name[..idx] : name;
    }

    private static bool ContainsTypeToken(string typeFullName, string token)
    {
        if (string.Equals(typeFullName, token, StringComparison.Ordinal))
        {
            return true;
        }

        return typeFullName.EndsWith("." + token, StringComparison.Ordinal)
               || typeFullName.EndsWith("/" + token, StringComparison.Ordinal)
               || typeFullName.Contains("<" + token + ">", StringComparison.Ordinal)
               || typeFullName.Contains("." + token + "`", StringComparison.Ordinal)
               || typeFullName.StartsWith(token + "`", StringComparison.Ordinal)
               || typeFullName.StartsWith(token + "[]", StringComparison.Ordinal);
    }

    private static AuditSummary BuildSummary(
        List<string> scannedAssemblies,
        int totalMonoBehaviours,
        List<RngCallerRecord> rngCallers,
        List<MutableStaticFieldRecord> mutableStaticFields,
        List<DdolCallRecord> ddolCalls,
        List<ComponentRiskRecord> componentRisks)
    {
        return new AuditSummary
        {
            ScannedAssemblyCount = scannedAssemblies.Count,
            TotalMonoBehaviours = totalMonoBehaviours,
            RngTotal = rngCallers.Count,
            RngCovered = rngCallers.Count(x => x.CoveredByLeanS0),
            RngUncovered = rngCallers.Count(x => !x.CoveredByLeanS0),
            RngUnityCount = rngCallers.Count(x => x.IsUnityRandom),
            RngSystemCount = rngCallers.Count(x => x.IsSystemRandom),
            RngMonoBehaviourCount = rngCallers.Count(x => x.IsMonoBehaviour),
            RngPlayMakerActionCount = rngCallers.Count(x => x.IsPlayMakerAction),
            RngLifecycleCount = rngCallers.Count(x => x.IsLifecycleMethod),
            RngByAssembly = rngCallers
                .GroupBy(x => x.Assembly)
                .OrderBy(g => g.Key, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Count()),
            StaticTotal = mutableStaticFields.Count,
            StaticCovered = mutableStaticFields.Count(x => x.CoveredByLeanS0),
            StaticUncovered = mutableStaticFields.Count(x => !x.CoveredByLeanS0),
            StaticByCategory = mutableStaticFields
                .GroupBy(x => x.Category)
                .OrderBy(g => g.Key, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Count()),
            DdolTotal = ddolCalls.Count,
            DdolCovered = ddolCalls.Count(x => x.CoveredByLeanS0),
            DdolUncovered = ddolCalls.Count(x => !x.CoveredByLeanS0),
            ComponentRiskTotal = componentRisks.Count,
            ComponentRiskCovered = componentRisks.Count(x => x.CoveredByLeanS0),
            ComponentRiskUncovered = componentRisks.Count(x => !x.CoveredByLeanS0),
            ComponentRiskByTag = componentRisks
                .SelectMany(x => x.RiskTags)
                .GroupBy(t => t)
                .OrderByDescending(g => g.Count())
                .ToDictionary(g => g.Key, g => g.Count())
        };
    }

    private static string RenderMarkdownReport(StaticAuditReport report)
    {
        var sb = new StringBuilder();
        AuditSummary s = report.Summary;

        sb.AppendLine("# LeanS0 离线静态扫描与清理清单对账报告");
        sb.AppendLine();
        sb.AppendLine($"- 生成时间 (UTC): `{report.GeneratedAtUtc}`");
        sb.AppendLine($"- Managed 目录: `{report.ManagedDir}`");
        sb.AppendLine($"- 已扫描程序集: {string.Join(", ", report.ScannedAssemblies.Select(a => $"`{a}`"))}");
        sb.AppendLine();
        sb.AppendLine("## 1. 总览统计");
        sb.AppendLine();
        sb.AppendLine("| 维度 | 总数 | LeanS0 已覆盖 | 未覆盖 | 备注 |");
        sb.AppendLine("| --- | ---: | ---: | ---: | --- |");
        sb.AppendLine($"| RNG 调用源 (`rngCallers`) | {s.RngTotal} | {s.RngCovered} | {s.RngUncovered} | UnityRandom={s.RngUnityCount}, SystemRandom={s.RngSystemCount}, MonoBehaviour={s.RngMonoBehaviourCount}, PlayMakerAction={s.RngPlayMakerActionCount}, Lifecycle={s.RngLifecycleCount} |");
        sb.AppendLine($"| 可变静态字段 (`mutableStaticFields`) | {s.StaticTotal} | {s.StaticCovered} | {s.StaticUncovered} | 仅 `Assembly-CSharp.dll`，已剔除 `<>c` 与 `__StaticArrayInitTypeSize` |");
        sb.AppendLine($"| `DontDestroyOnLoad` 调用点 (`ddolCalls`) | {s.DdolTotal} | {s.DdolCovered} | {s.DdolUncovered} | 仅 `Assembly-CSharp.dll` |");
        sb.AppendLine($"| 高风险 `MonoBehaviour` (`componentRisks`) | {s.ComponentRiskTotal} | {s.ComponentRiskCovered} | {s.ComponentRiskUncovered} | 共扫描 `MonoBehaviour`={s.TotalMonoBehaviours} |");
        sb.AppendLine();

        sb.AppendLine("### 1.1 RNG 调用源按程序集分布");
        sb.AppendLine();
        foreach (KeyValuePair<string, int> kv in s.RngByAssembly)
        {
            sb.AppendLine($"- `{kv.Key}`: {kv.Value}");
        }
        sb.AppendLine();

        sb.AppendLine("### 1.2 可变静态字段按类别分布");
        sb.AppendLine();
        foreach (KeyValuePair<string, int> kv in s.StaticByCategory)
        {
            int covered = report.MutableStaticFields.Count(x => x.Category == kv.Key && x.CoveredByLeanS0);
            sb.AppendLine($"- `{kv.Key}`: {kv.Value} (已覆盖 {covered}, 未覆盖 {kv.Value - covered})");
        }
        sb.AppendLine();

        sb.AppendLine("### 1.3 组件风险标签分布");
        sb.AppendLine();
        foreach (KeyValuePair<string, int> kv in s.ComponentRiskByTag)
        {
            sb.AppendLine($"- `{kv.Key}`: {kv.Value}");
        }
        sb.AppendLine();

        sb.AppendLine("## 2. 未覆盖高风险 RNG 调用源");
        sb.AppendLine();
        sb.AppendLine("### 2.1 MonoBehaviour / 生命周期未覆盖 RNG（Top 40）");
        sb.AppendLine();
        List<RngCallerRecord> topUncoveredMonoRng = report.RngCallers
            .Where(x => !x.CoveredByLeanS0 && (x.IsLifecycleMethod || x.IsMonoBehaviour))
            .OrderByDescending(x => x.IsLifecycleMethod)
            .ThenByDescending(x => x.IsMonoBehaviour)
            .ThenBy(x => x.Assembly, StringComparer.Ordinal)
            .ThenBy(x => x.TypeName, StringComparer.Ordinal)
            .Take(40)
            .ToList();

        sb.AppendLine("| Assembly | Type | Method | Target | MonoBehaviour | Lifecycle |");
        sb.AppendLine("| --- | --- | --- | --- | :---: | :---: |");
        foreach (RngCallerRecord r in topUncoveredMonoRng)
        {
            sb.AppendLine($"| `{r.Assembly}` | `{r.TypeName}` | `{r.MethodName}` | `{r.RandomTarget}` | {(r.IsMonoBehaviour ? "Y" : "-")} | {(r.IsLifecycleMethod ? "Y" : "-")} |");
        }
        sb.AppendLine();

        sb.AppendLine("### 2.2 PlayMaker Action 未覆盖 RNG（Top 30）");
        sb.AppendLine();
        List<RngCallerRecord> topUncoveredActionRng = report.RngCallers
            .Where(x => !x.CoveredByLeanS0 && x.IsPlayMakerAction)
            .OrderBy(x => x.Assembly, StringComparer.Ordinal)
            .ThenBy(x => x.TypeName, StringComparer.Ordinal)
            .Take(30)
            .ToList();

        sb.AppendLine("| Assembly | Type | Method | Target |");
        sb.AppendLine("| --- | --- | --- | --- |");
        foreach (RngCallerRecord r in topUncoveredActionRng)
        {
            sb.AppendLine($"| `{r.Assembly}` | `{r.TypeName}` | `{r.MethodName}` | `{r.RandomTarget}` |");
        }
        sb.AppendLine();

        sb.AppendLine("## 3. 可变静态字段重点清单（已覆盖对账 + 未覆盖重点）");
        sb.AppendLine();
        sb.AppendLine("### 3.1 LeanS0 已覆盖静态字段");
        sb.AppendLine();
        foreach (MutableStaticFieldRecord f in report.MutableStaticFields.Where(x => x.CoveredByLeanS0))
        {
            sb.AppendLine($"- `{f.TypeName}.{f.FieldName}` (`{f.FieldType}`, `{f.Category}`)");
        }
        sb.AppendLine();

        sb.AppendLine("### 3.2 未覆盖游戏原生重点静态字段（排除 `Modding.*` / `InControl.*`，Top 60）");
        sb.AppendLine();
        string[] priorityCategories = ["StaticRandom", "StaticDelegateOrEvent", "StaticCollection", "StaticTimerOrCounter"];
        List<MutableStaticFieldRecord> topUncoveredStatics = report.MutableStaticFields
            .Where(x => !x.CoveredByLeanS0
                        && priorityCategories.Contains(x.Category)
                        && !x.TypeName.StartsWith("Modding.", StringComparison.Ordinal)
                        && !x.TypeName.StartsWith("InControl.", StringComparison.Ordinal))
            .OrderBy(x => Array.IndexOf(priorityCategories, x.Category))
            .ThenBy(x => x.TypeName, StringComparer.Ordinal)
            .ThenBy(x => x.FieldName, StringComparer.Ordinal)
            .Take(60)
            .ToList();

        sb.AppendLine("| Category | Type | Field | FieldType |");
        sb.AppendLine("| --- | --- | --- | --- |");
        foreach (MutableStaticFieldRecord f in topUncoveredStatics)
        {
            sb.AppendLine($"| `{f.Category}` | `{f.TypeName}` | `{f.FieldName}` | `{f.FieldType}` |");
        }
        sb.AppendLine();

        sb.AppendLine("## 4. `DontDestroyOnLoad` 调用点清单");
        sb.AppendLine();
        sb.AppendLine("| Type | Method | MonoBehaviour | Lifecycle | CoveredByLeanS0 |");
        sb.AppendLine("| --- | --- | :---: | :---: | :---: |");
        foreach (DdolCallRecord d in report.DdolCalls)
        {
            sb.AppendLine($"| `{d.TypeName}` | `{d.MethodName}` | {(d.IsMonoBehaviour ? "Y" : "-")} | {(d.IsLifecycleMethod ? "Y" : "-")} | {(d.CoveredByLeanS0 ? "Y" : "-")} |");
        }
        sb.AppendLine();

        sb.AppendLine("## 5. 未覆盖高风险 `MonoBehaviour` 组件（Top 50，按风险标签数降序）");
        sb.AppendLine();
        List<ComponentRiskRecord> topUncoveredComponents = report.ComponentRisks
            .Where(x => !x.CoveredByLeanS0)
            .Take(50)
            .ToList();

        sb.AppendLine("| Type | RiskCount | RiskTags |");
        sb.AppendLine("| --- | ---: | --- |");
        foreach (ComponentRiskRecord c in topUncoveredComponents)
        {
            sb.AppendLine($"| `{c.TypeName}` | {c.RiskTags.Count} | {string.Join(", ", c.RiskTags.Select(t => $"`{t}`"))} |");
        }
        sb.AppendLine();

        return sb.ToString();
    }
}

internal sealed class StaticAuditReport
{
    public string GeneratedAtUtc { get; set; } = string.Empty;
    public string ManagedDir { get; set; } = string.Empty;
    public List<string> ScannedAssemblies { get; set; } = new();
    public AuditSummary Summary { get; set; } = new();
    public List<RngCallerRecord> RngCallers { get; set; } = new();
    public List<MutableStaticFieldRecord> MutableStaticFields { get; set; } = new();
    public List<DdolCallRecord> DdolCalls { get; set; } = new();
    public List<ComponentRiskRecord> ComponentRisks { get; set; } = new();
}

internal sealed class AuditSummary
{
    public int ScannedAssemblyCount { get; set; }
    public int TotalMonoBehaviours { get; set; }
    public int RngTotal { get; set; }
    public int RngCovered { get; set; }
    public int RngUncovered { get; set; }
    public int RngUnityCount { get; set; }
    public int RngSystemCount { get; set; }
    public int RngMonoBehaviourCount { get; set; }
    public int RngPlayMakerActionCount { get; set; }
    public int RngLifecycleCount { get; set; }
    public Dictionary<string, int> RngByAssembly { get; set; } = new();
    public int StaticTotal { get; set; }
    public int StaticCovered { get; set; }
    public int StaticUncovered { get; set; }
    public Dictionary<string, int> StaticByCategory { get; set; } = new();
    public int DdolTotal { get; set; }
    public int DdolCovered { get; set; }
    public int DdolUncovered { get; set; }
    public int ComponentRiskTotal { get; set; }
    public int ComponentRiskCovered { get; set; }
    public int ComponentRiskUncovered { get; set; }
    public Dictionary<string, int> ComponentRiskByTag { get; set; } = new();
}

internal sealed class RngCallerRecord
{
    public string Assembly { get; set; } = string.Empty;
    public string TypeName { get; set; } = string.Empty;
    public string MethodName { get; set; } = string.Empty;
    public string RandomTarget { get; set; } = string.Empty;
    public bool IsUnityRandom { get; set; }
    public bool IsSystemRandom { get; set; }
    public bool IsMonoBehaviour { get; set; }
    public bool IsPlayMakerAction { get; set; }
    public bool IsLifecycleMethod { get; set; }
    public bool CoveredByLeanS0 { get; set; }
}

internal sealed class MutableStaticFieldRecord
{
    public string TypeName { get; set; } = string.Empty;
    public string FieldName { get; set; } = string.Empty;
    public string FieldType { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public bool CoveredByLeanS0 { get; set; }
}

internal sealed class DdolCallRecord
{
    public string TypeName { get; set; } = string.Empty;
    public string MethodName { get; set; } = string.Empty;
    public bool IsMonoBehaviour { get; set; }
    public bool IsLifecycleMethod { get; set; }
    public bool CoveredByLeanS0 { get; set; }
}

internal sealed class ComponentRiskRecord
{
    public string TypeName { get; set; } = string.Empty;
    public string SimpleName { get; set; } = string.Empty;
    public List<string> RiskTags { get; set; } = new();
    public bool CoveredByLeanS0 { get; set; }
}
