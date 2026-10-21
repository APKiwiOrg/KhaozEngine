"""Generate test-only instrumented copies. Product sources are never modified."""
from pathlib import Path
import hashlib
import subprocess

root = Path(__file__).resolve().parents[3]
out = root / "KhaozEngine.Render.Tests/obj/coverage-filter-study"
out.mkdir(parents=True, exist_ok=True)
names = ("Arithmetic", "Math", "Support", "Correction", "Normals")
digests = {}
for name in names:
    path = root / f"KhaozEngine.Render3D/Coverage/CoverageReference{name}.cs"
    original = path.read_text()
    pinned = subprocess.check_output(["git", "show", f"f1a298ef1:{path.relative_to(root)}"], cwd=root).decode()
    assert original == pinned, f"Study source drift: {path}"
    digests[str(path.relative_to(root))] = hashlib.sha256(original.encode()).hexdigest()
    text = original.replace("namespace KhaozEngine.Render3D;", "using KhaozEngine.Render3D;\nusing System.Runtime.CompilerServices;\nnamespace KhaozEngine.Tests.Gpu.FilterStudy;")
    for other in names:
        text = text.replace(f"CoverageReference{other}", f"Study{other}")
    text = text.replace("[Binary32Kernel]", "").replace("[Binary32Arithmetic]", "")
    if name == "Arithmetic":
        text = text.replace("internal readonly record struct CoverageReferenceScalar(int Sign, float Value);", "")
        text = text.replace("this.phase = phase; this.element", "if (phase != CoverageExactnessPhase.FaceCorrection) StudyContext.Stage = phase.ToString();\n        this.phase = phase; this.element")
        # Use the actual reference operation and observe only after its inputs are flushed.
        text = text.replace("Vector3 p)\n    {", 'Vector3 p, [CallerMemberName] string caller = "")\n    {')
        text = text.replace("Vector3 d)\n    {", 'Vector3 d, [CallerMemberName] string caller = "")\n    {')
        for kind, args in (("Edge", "a, b, p"), ("Dot", "a, b, c, d")):
            needle = f"var result = CoverageReferencePredicates.{kind}({args});"
            text = text.replace(needle, needle + f"\n        StudyContext.Predicate(\"{kind}\", caller, [{args}], result);")
        for method, expression in (("Div", "a / b"), ("Sqrt", "MathF.Sqrt(a)")):
            signature = "float a, float b" if method == "Div" else "float a"
            text = text.replace(f"internal float {method}({signature})", f'internal float {method}({signature}, [CallerMemberName] string caller = "")')
            text = text.replace(f"return Flush({expression}, op);", f"float result = Flush({expression}, op);\n        StudyContext.Scalar(\"{method}\", caller, a, " + ("b" if method == "Div" else "0") + ", result);\n        return result;")
        needle = "x = Flush(x, op); y = Flush(y, op);"
        text = text.replace(needle, "StudyContext.Crossing(s, t, a, b, x, y);\n        " + needle)
        for method in ("Max", "Min"):
            needle = f"return Flush(MathF.{method}(a, b), op);"
            text = text.replace(needle, f"StudyContext.Ordinary(\"{method}\", a, b);\n        " + needle)
        for symbol in ("<", "<=", ">", ">=", "==", "!="):
            needle = f"return a {symbol} b;"
            text = text.replace(needle, 'StudyContext.Ordinary("Compare", a, b);\n        ' + needle)
    elif name == "Math":
        needle = "internal static float Height(StudyArithmetic f, Vector3 a, Vector3 b, Vector3 c, Vector2 p)\n    {"
        text = text.replace(needle, needle + '\n        using var context = StudyContext.Enter("Height");')
        text = text.replace("f.Edge(a, b, c).Value", "CoverageReferencePredicates.Edge(a, b, c).Value")
    elif name == "Support":
        needle = "internal bool TryHeight(StudyArithmetic f, Vector2 point, out float height, out int sourceTriangle)\n    {"
        text = text.replace(needle, needle + '\n        using var context = StudyContext.Enter("TryHeight");')
        text = text.replace("CoverageReferenceScalar area = f.Edge(a, b, c)", "CoverageReferenceScalar area = CoverageReferencePredicates.Edge(a, b, c)")
        text = text.replace("CoverageReferenceScalar sheetArea = f.Edge(sheet[0], sheet[1], sheet[2]);", "CoverageReferenceScalar sheetArea = CoverageReferencePredicates.Edge(sheet[0], sheet[1], sheet[2]);")
        text = text.replace("var triangle = Triangles[t];\n            sheet[0]", "StudyContext.BeginPair();\n            var triangle = Triangles[t];\n            sheet[0]")
    elif name == "Correction":
        text = text.replace("StudyCorrectionWork", "CoverageReferenceCorrectionWork").replace("StudyCorrectionPhase", "CoverageReferenceCorrectionPhase")
        text = text.replace("scratch.Arithmetic", "StudyContext.Arithmetic")
        text = text.replace("command.Support.ReferenceData", "StudyContext.Support(command.Support)")
        text = text.replace("scratch.FaceGuards[t] = TryFace", "StudyContext.BeginFace();\n                scratch.FaceGuards[t] = TryFace")
        text = text.replace("if (!PrepareFace(f,", 'StudyContext.Stage = "CorrectFaces";\n        if (!PrepareFace(f,')
        text = text.replace("if (!support.TryOverlayMaximum", 'StudyContext.Stage = "Overlay";\n        if (!support.TryOverlayMaximum')
        text = text.replace("return FinishFace(f,", 'StudyContext.Stage = "Finish";\n        return FinishFace(f,')
        text = text.replace("for (int index = 0;", "StudyContext.EndFace();\n        for (int index = 0;")
    elif name == "Normals":
        text = text.replace("scratch.Arithmetic", "StudyContext.Arithmetic")
    (out / f"Study{name}.g.cs").write_text("#nullable enable\n" + text)
print("Generated five test-only copies", digests)
