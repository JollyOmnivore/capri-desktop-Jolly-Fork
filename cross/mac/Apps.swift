import AppKit
import CoreGraphics
import Foundation

struct AppEntry: Codable {
    let name: String
    let path: String
    let bundleId: String
    let aliases: [String]
}

struct LaunchResult: Codable {
    let status: String
    let visible: Bool
    let activated: Bool
    let message: String
}

func emit<T: Encodable>(_ value: T) {
    if let data = try? JSONEncoder().encode(value) {
        FileHandle.standardOutput.write(data)
        FileHandle.standardOutput.write(Data([10]))
    }
}

func physical(_ url: URL) -> URL {
    return url.standardizedFileURL.resolvingSymlinksInPath()
}

func entry(_ url: URL) -> AppEntry {
    let bundle = Bundle(url: url)
    let filename = url.deletingPathExtension().lastPathComponent
    let display = bundle?.object(forInfoDictionaryKey: "CFBundleDisplayName") as? String
    let name = bundle?.object(forInfoDictionaryKey: "CFBundleName") as? String
    return AppEntry(name: [display, name, filename].compactMap { $0 }.first { !$0.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty } ?? filename,
                    path: physical(url).path, bundleId: bundle?.bundleIdentifier ?? "", aliases: [filename, name].compactMap { $0 })
}

func catalog(_ roots: [String]) -> [AppEntry] {
    var apps: [String: AppEntry] = [:]
    let manager = FileManager.default
    for root in roots {
        let base = URL(fileURLWithPath: root, isDirectory: true)
        if base.pathExtension.lowercased() == "app" {
            if manager.fileExists(atPath: base.path) { let app = entry(base); apps[app.path] = app }
            continue
        }
        guard let files = manager.enumerator(at: base, includingPropertiesForKeys: [.isDirectoryKey, .isSymbolicLinkKey], options: [.skipsHiddenFiles, .skipsPackageDescendants], errorHandler: { _, _ in true }) else { continue }
        var visited = 0
        for case let url as URL in files {
            visited += 1
            if visited > 10000 { break }
            if url.pathExtension.lowercased() == "app" {
                let app = entry(url)
                apps[app.path] = app
                files.skipDescendants()
            } else if files.level >= 4 { files.skipDescendants() }
        }
    }
    return apps.values.sorted { $0.path < $1.path }
}

func running(_ url: URL) -> NSRunningApplication? {
    return NSWorkspace.shared.runningApplications.first { app in
        !app.isTerminated && app.bundleURL.map { physical($0) == url } == true
    }
}

func visible(_ pid: pid_t) -> Bool {
    guard let windows = CGWindowListCopyWindowInfo(.optionOnScreenOnly, kCGNullWindowID) as? [[String: Any]] else { return false }
    return windows.contains { window in
        guard (window[kCGWindowOwnerPID as String] as? Int) == Int(pid),
              (window[kCGWindowLayer as String] as? Int) == 0,
              let bounds = window[kCGWindowBounds as String] as? [String: Any],
              let width = bounds["Width"] as? Double, let height = bounds["Height"] as? Double else { return false }
        return width > 1 && height > 1
    }
}

func launch(_ path: String) -> LaunchResult {
    let url = physical(URL(fileURLWithPath: path, isDirectory: true))
    guard path.hasPrefix("/"), url.pathExtension.lowercased() == "app" else { return LaunchResult(status: "invalid", visible: false, activated: false, message: "Not an absolute app bundle path.") }
    guard FileManager.default.fileExists(atPath: url.path) else { return LaunchResult(status: "missing", visible: false, activated: false, message: "The selected app has moved or was removed.") }
    guard let bundle = Bundle(url: url), let executable = bundle.executableURL,
          FileManager.default.isExecutableFile(atPath: executable.path) else { return LaunchResult(status: "invalid", visible: false, activated: false, message: "The bundle has no executable application.") }
    if let app = running(url) {
        let activated = app.activate(options: [])
        RunLoop.current.run(until: Date().addingTimeInterval(0.25))
        guard let current = running(url) else { return LaunchResult(status: "unconfirmed", visible: false, activated: false, message: "The app exited while being checked.") }
        return LaunchResult(status: "already_running", visible: visible(current.processIdentifier), activated: activated && current.isActive, message: "")
    }
    let options = NSWorkspace.OpenConfiguration()
    options.activates = true
    options.addsToRecentItems = false
    options.promptsUserIfNeeded = false
    options.createsNewApplicationInstance = false
    options.allowsRunningApplicationSubstitution = false
    var finished = false
    var failure: String?
    NSWorkspace.shared.openApplication(at: url, configuration: options) { app, error in
        failure = error?.localizedDescription
        finished = true
    }
    let deadline = Date().addingTimeInterval(12)
    while !finished && Date() < deadline { RunLoop.current.run(until: Date().addingTimeInterval(0.05)) }
    if !finished { return LaunchResult(status: "unconfirmed", visible: false, activated: false, message: "Launch Services did not answer in time; the app may still start.") }
    if let failure = failure { return LaunchResult(status: "failed", visible: false, activated: false, message: failure) }
    RunLoop.current.run(until: Date().addingTimeInterval(0.6))
    guard let app = running(url) else { return LaunchResult(status: "unconfirmed", visible: false, activated: false, message: "The selected app was not running when checked; it may have exited or still be starting.") }
    return LaunchResult(status: "opened", visible: visible(app.processIdentifier), activated: app.isActive, message: "")
}

let arguments = Array(CommandLine.arguments.dropFirst())
if arguments.first == "--catalog" {
    emit(catalog(Array(arguments.dropFirst())))
} else if arguments.count == 2 && arguments[0] == "--launch" {
    emit(launch(arguments[1]))
} else {
    FileHandle.standardError.write(Data("Use --catalog folders... or --launch /absolute/App.app\n".utf8))
    exit(2)
}
