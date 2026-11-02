import AVFoundation
import Foundation

func fail(_ message: String) -> Never {
    FileHandle.standardError.write(Data((message + "\n").utf8))
    exit(1)
}

if CommandLine.arguments.count == 2 && CommandLine.arguments[1] == "--check" {
    guard Bundle.main.object(forInfoDictionaryKey: "NSMicrophoneUsageDescription") != nil else { fail("Missing microphone permission description.") }
    print("capri-mic ready")
    exit(0)
}
guard CommandLine.arguments.count == 2 else { fail("Missing recording path.") }
let path = CommandLine.arguments[1]
let lock = NSLock()
var stopped = false
func shouldStop() -> Bool {
    lock.lock()
    defer { lock.unlock() }
    return stopped
}
DispatchQueue.global().async {
    _ = readLine()
    lock.lock()
    stopped = true
    lock.unlock()
}

var allowed = AVCaptureDevice.authorizationStatus(for: .audio) == .authorized
if AVCaptureDevice.authorizationStatus(for: .audio) == .notDetermined {
    var answered = false
    AVCaptureDevice.requestAccess(for: .audio) { granted in
        lock.lock()
        allowed = granted
        answered = true
        lock.unlock()
    }
    while true {
        lock.lock()
        let done = answered
        lock.unlock()
        if done { break }
        RunLoop.current.run(until: Date(timeIntervalSinceNow: 0.05))
    }
}
guard allowed else { fail("Allow Capri under System Settings > Privacy & Security > Microphone, then hold TALK again.") }
if shouldStop() { exit(0) }

let settings: [String: Any] = [
    AVFormatIDKey: kAudioFormatLinearPCM,
    AVSampleRateKey: 16000,
    AVNumberOfChannelsKey: 1,
    AVLinearPCMBitDepthKey: 16,
    AVLinearPCMIsFloatKey: false,
    AVLinearPCMIsBigEndianKey: false
]
do {
    let recorder = try AVAudioRecorder(url: URL(fileURLWithPath: path), settings: settings)
    guard recorder.prepareToRecord() else { fail("Couldn't prepare the microphone.") }
    if shouldStop() { exit(0) }
    guard recorder.record(forDuration: 30) else { fail("Couldn't start the microphone. Check your Mac's sound input.") }
    try FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: path)
    print("READY")
    fflush(stdout)
    let deadline = Date(timeIntervalSinceNow: 30)
    while !shouldStop() && recorder.isRecording && Date() < deadline {
        RunLoop.current.run(until: Date(timeIntervalSinceNow: 0.03))
    }
    recorder.stop()
} catch {
    fail("Microphone recording failed: \(error.localizedDescription)")
}
