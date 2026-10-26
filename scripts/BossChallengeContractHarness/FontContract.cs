using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Text.Json;
using TMPro;
using UnityEngine;
using Obj = UnityEngine.Object;

internal static class FontContract
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly Type Owner = typeof(ChangeFontByLanguage);
    private static readonly Type ScaleType = Owner.GetNestedType("FontScaleLang", Flags)!;
    private static readonly FieldInfo Size = typeof(TMP_Text).GetField("m_fontSize", Flags)!;
    private static readonly FieldInfo Font = typeof(TMP_Text).GetField("m_fontAsset", Flags)!;
    private static readonly FieldInfo Baseline = Owner.GetField("startFontSize", Flags)!;
    private static readonly Dictionary<ChangeFontByLanguage, TextMeshPro> Texts = new();
    private static readonly Dictionary<string, DynamicMethod> Original = new();
    public static object LanguageState = null!;
    private static TextMeshPro Text(Component component) => Texts[(ChangeFontByLanguage)component];
    private static bool Live(Obj value) => !ReferenceEquals(value, null);
    private static bool Equal(Obj one, Obj two) => ReferenceEquals(one, two);
    private static TMP_FontAsset GetFont(TMP_Text text) => (TMP_FontAsset)Font.GetValue(text)!;
    private static void SetFont(TMP_Text text, TMP_FontAsset font) => Font.SetValue(text, font);
    private static float GetSize(TMP_Text text) => (float)Size.GetValue(text)!;
    private static void SetSize(TMP_Text text, float value)
    { Size.SetValue(text, value); typeof(TMP_Text).GetField("m_fontSizeBase", Flags)!.SetValue(text, value); }
    private static MethodInfo? Replace(MethodInfo method)
    {
        if (method.DeclaringType == Owner && Original.TryGetValue(method.Name, out var copied)) return copied;
        if (method.DeclaringType == ScaleType && method.Name == "GetFontScale") return Original["scale"];
        string? mock = method.DeclaringType == typeof(Obj) ? method.Name switch { "op_Implicit" => nameof(Live), "op_Equality" => nameof(Equal), _ => null }
            : method.DeclaringType == typeof(Component) && method.Name == "GetComponent" ? nameof(Text)
            : method.DeclaringType == typeof(TMP_Text) ? method.Name switch { "get_font" => nameof(GetFont), "set_font" => nameof(SetFont), "get_fontSize" => nameof(GetSize), "set_fontSize" => nameof(SetSize), _ => null } : null;
        if (mock != null) return typeof(FontContract).GetMethod(mock, Flags)!;
        if (method.Name == "CurrentLanguage" && method.DeclaringType!.FullName == "Language.Language") {
            var result = new DynamicMethod("MockCurrentLanguage", method.ReturnType, Type.EmptyTypes, typeof(FontContract).Module, true);
            var il = result.GetILGenerator(); il.Emit(OpCodes.Ldsfld, typeof(FontContract).GetField(nameof(LanguageState), Flags)!);
            il.Emit(OpCodes.Unbox_Any, method.ReturnType); il.Emit(OpCodes.Ret); return result;
        }
        return null;
    }
    private static T Blank<T>() => (T)RuntimeHelpers.GetUninitializedObject(typeof(T));
    private static ChangeFontByLanguage Make(float baseSize, int kind, bool assigned, bool alias)
    {
        var owner = Blank<ChangeFontByLanguage>(); var text = Blank<TextMeshPro>(); Texts.Add(owner, text);
        Size.SetValue(text, baseSize); Font.SetValue(text, Blank<TMP_FontAsset>());
        owner.fontScaleLangType = (ChangeFontByLanguage.FontScaleLangTypes)kind; owner.onlyOnStart = false;
        owner.defaultFont = assigned ? Blank<TMP_FontAsset>() : null;
        var shared = Blank<TMP_FontAsset>();
        owner.fontJA = alias ? shared : Blank<TMP_FontAsset>(); owner.fontRU = alias ? shared : Blank<TMP_FontAsset>();
        owner.fontZH = alias ? shared : Blank<TMP_FontAsset>(); owner.fontKO = alias ? shared : Blank<TMP_FontAsset>();
        var definitions = new (string name, float?[] values)[] {
            ("fontScaleAreaName",new float?[]{4.5f,2.8f,4.5f,4.5f}), ("fontScaleSubAreaName",new float?[]{null,3.9f,null,null}),
            ("fontScaleWideMap",new float?[]{2.5f,null,3.14f,2.89f}), ("fontScaleCreditsTitle",new float?[]{null,5.5f,null,null}),
            ("fontScaleExcerptAuthor",new float?[]{4.5f,4.5f,4.5f,4.5f}) };
        foreach (var definition in definitions) {
            var data = RuntimeHelpers.GetUninitializedObject(ScaleType);
            var names = new[]{"fontSizeJA","fontSizeRU","fontSizeZH","fontSizeKO"};
            for (var index=0;index<4;index++) ScaleType.GetField(names[index],Flags)!.SetValue(data,definition.values[index]);
            Owner.GetField(definition.name,Flags)!.SetValue(owner,data);
        }
        return owner;
    }
    private static void Invoke(string method, ChangeFontByLanguage owner) => Original[method].Invoke(null, new object[]{owner});
    private static bool Bits(float a, float b) => BitConverter.SingleToInt32Bits(a)==BitConverter.SingleToInt32Bits(b);
    internal static void Run(string runtimePath)
    {
        if (Owner.Module.ModuleVersionId != new Guid("2b54cdaf-7a93-4f0e-b297-d50c4090ea9f")) throw new Exception("Original font module differs");
        Original["scale"] = OriginalIL.Copy(ScaleType.GetMethod("GetFontScale", Flags)!, Replace);
        Original["GetFontScale"] = OriginalIL.Copy(Owner.GetMethod("GetFontScale", Flags)!, Replace);
        Original["SetFont"] = OriginalIL.Copy(Owner.GetMethod("SetFont", Flags)!, Replace);
        foreach(var name in new[]{"Awake","OnEnable","Start"}) Original[name]=OriginalIL.Copy(Owner.GetMethod(name,Flags)!,Replace);
        var runtimeDirectory=Path.GetDirectoryName(Path.GetFullPath(runtimePath))!;
        Assembly.LoadFrom(Path.Combine(runtimeDirectory,"HollowKnightTAS.Core.dll"));
        var runtime=Assembly.LoadFrom(Path.GetFullPath(runtimePath));
        var install=runtime.GetType("HollowKnightTAS.Runtime.FullRun.CanonicalHudBorderInputs",true)!.GetMethod("InstallOriginalFontBaseline",Flags)!;
        var languageMethod=Owner.Assembly.GetType("Language.Language",true)!.GetMethod("CurrentLanguage",Flags)!;
        var languageType=languageMethod.ReturnType; var languages=new[]{"EN","JA","KO","RU","ZH","ES"};
        int cases=0,future=0,badDetected=0;
        foreach(var kind in Enumerable.Range(0,6)) foreach(var language in languages) foreach(var baseSize in new[]{2.5f,8.3f,22f})
        foreach(var assigned in new[]{false,true}) foreach(var alias in new[]{false,true}) {
            var old=Make(baseSize,kind,assigned,alias); LanguageState=Enum.Parse(languageType,language);
            Invoke("Awake",old);Invoke("OnEnable",old);Invoke("Start",old);
            var oldText=Texts[old]; var displayed=GetSize(oldText);var originalBaseline=(float)Baseline.GetValue(old)!;
            var fresh=Make(displayed,kind,true,alias);var freshText=Texts[fresh];
            fresh.defaultFont=old.defaultFont;fresh.fontJA=old.fontJA;fresh.fontRU=old.fontRU;fresh.fontKO=old.fontKO;fresh.fontZH=old.fontZH;
            Font.SetValue(freshText,Font.GetValue(oldText));install.Invoke(null,new object[]{freshText,originalBaseline});
            Invoke("Awake",fresh);Invoke("OnEnable",fresh);Invoke("Start",fresh);
            if(!Bits(GetSize(freshText),displayed)||!Bits((float)Baseline.GetValue(fresh)!,originalBaseline)||!ReferenceEquals(Font.GetValue(freshText),Font.GetValue(oldText)))
                throw new Exception("Production pre-Awake input lost current font or future fallback baseline");
            foreach(var next in languages) {
                LanguageState=Enum.Parse(languageType,next);Invoke("SetFont",old);Invoke("SetFont",fresh);
                if(!Bits(GetSize(oldText),GetSize(freshText))||!ReferenceEquals(Font.GetValue(oldText),Font.GetValue(freshText)))throw new Exception("Future language behavior differs");
                future++;
            }
            if(!Bits(displayed,originalBaseline)) badDetected++;
            Texts.Remove(old);Texts.Remove(fresh);cases++;
        }
        Console.WriteLine(JsonSerializer.Serialize(new{originalFontILCases=cases,futureLanguageTransitions=future,wrongDisplayedBaselineCounterexamples=badDetected,
            productionPreAwakeBaselineInvoked=true,originalAwakeEnableStartAndSetFont=true,nativeCallsOrGameRun=false,otherNativeLifecycleNotTested=true}));
    }
}
