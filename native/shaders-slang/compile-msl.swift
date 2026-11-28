// Compiles each MSL file through the Metal framework at runtime, the way the engine does, and fails on any
// error. The runtime compiler reports no warnings for a library it builds, so the -Wall and -Wextra groups
// are raised to errors in the source. Usage: xcrun swift compile-msl.swift <file.metal>...
import Foundation
import Metal

guard let device = MTLCreateSystemDefaultDevice() else {
    FileHandle.standardError.write("no Metal device\n".data(using: .utf8)!)
    exit(2)
}

// The pins in KhaozEngine.Gpu.Metal/Internal/MslCompilePin.cs. fastMathEnabled goes through key-value coding
// because the engine sends that selector and its Swift property is deprecated.
let options = MTLCompileOptions()
options.languageVersion = .version3_2
options.setValue(true, forKey: "fastMathEnabled")
options.preserveInvariance = false

var failed = false
func fail(_ message: String) {
    FileHandle.standardError.write(Data("error: \(message)\n".utf8))
    failed = true
}

for path in CommandLine.arguments.dropFirst() {
    guard let text = try? String(contentsOfFile: path, encoding: .utf8) else {
        fail("cannot read \(path)")
        continue
    }
    let source = "#pragma clang diagnostic error \"-Wall\"\n#pragma clang diagnostic error \"-Wextra\"\n" + text
    let done = DispatchSemaphore(value: 0)
    var library: MTLLibrary?
    var diagnostic: Error?
    device.makeLibrary(source: source, options: options) { result, error in
        library = result
        diagnostic = error
        done.signal()
    }
    done.wait()
    if let diagnostic {
        fail("\(path): \(diagnostic.localizedDescription)")
    } else if let library {
        print("ok: \(path): \(library.functionNames.sorted().joined(separator: ", "))")
    } else {
        fail("\(path): no library and no diagnostic")
    }
}
exit(failed ? 1 : 0)
