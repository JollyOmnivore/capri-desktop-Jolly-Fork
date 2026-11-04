import AVFoundation
import AudioToolbox
import CoreAudio
import Foundation

func fail(_ message: String) -> Never {
    FileHandle.standardError.write(Data((message + "\n").utf8))
    exit(1)
}

struct InputDevice: Codable {
    let id: String
    let name: String
    let isDefault: Bool
}

func property(_ selector: AudioObjectPropertySelector, _ scope: AudioObjectPropertyScope = kAudioObjectPropertyScopeGlobal) -> AudioObjectPropertyAddress {
    return AudioObjectPropertyAddress(mSelector: selector, mScope: scope, mElement: kAudioObjectPropertyElementMain)
}

func deviceText(_ device: AudioDeviceID, _ selector: AudioObjectPropertySelector) -> String? {
    var address = property(selector)
    var value: CFString = "" as CFString
    var size = UInt32(MemoryLayout<CFString>.size)
    let result = withUnsafeMutablePointer(to: &value) { AudioObjectGetPropertyData(device, &address, 0, nil, &size, $0) }
    guard result == noErr else { return nil }
    return value as String
}

func inputDevices() -> [InputDevice] {
    var address = property(kAudioHardwarePropertyDevices)
    var size: UInt32 = 0
    guard AudioObjectGetPropertyDataSize(AudioObjectID(kAudioObjectSystemObject), &address, 0, nil, &size) == noErr else { return [] }
    var devices = [AudioDeviceID](repeating: 0, count: Int(size) / MemoryLayout<AudioDeviceID>.size)
    guard AudioObjectGetPropertyData(AudioObjectID(kAudioObjectSystemObject), &address, 0, nil, &size, &devices) == noErr else { return [] }
    var defaultDevice: AudioDeviceID = 0
    var defaultAddress = property(kAudioHardwarePropertyDefaultInputDevice)
    var defaultSize = UInt32(MemoryLayout<AudioDeviceID>.size)
    _ = AudioObjectGetPropertyData(AudioObjectID(kAudioObjectSystemObject), &defaultAddress, 0, nil, &defaultSize, &defaultDevice)
    var found: [InputDevice] = []
    for device in devices {
        var streamAddress = property(kAudioDevicePropertyStreamConfiguration, kAudioObjectPropertyScopeInput)
        var streamSize: UInt32 = 0
        guard AudioObjectGetPropertyDataSize(device, &streamAddress, 0, nil, &streamSize) == noErr, streamSize >= MemoryLayout<AudioBufferList>.size else { continue }
        let memory = UnsafeMutableRawPointer.allocate(byteCount: Int(streamSize), alignment: MemoryLayout<AudioBufferList>.alignment)
        defer { memory.deallocate() }
        let buffers = memory.bindMemory(to: AudioBufferList.self, capacity: 1)
        guard AudioObjectGetPropertyData(device, &streamAddress, 0, nil, &streamSize, buffers) == noErr else { continue }
        guard UnsafeMutableAudioBufferListPointer(buffers).contains(where: { $0.mNumberChannels > 0 }), let uid = deviceText(device, kAudioDevicePropertyDeviceUID), !uid.isEmpty else { continue }
        found.append(InputDevice(id: uid, name: deviceText(device, kAudioObjectPropertyName) ?? "Microphone", isDefault: device == defaultDevice))
    }
    return found.sorted { $0.name.localizedCaseInsensitiveCompare($1.name) == .orderedAscending }
}

func wave(_ samples: Data) -> Data {
    var data = Data("RIFF".utf8)
    func number<T: FixedWidthInteger>(_ number: T) {
        var little = number.littleEndian
        withUnsafeBytes(of: &little) { data.append(contentsOf: $0) }
    }
    number(UInt32(samples.count + 36))
    data.append(Data("WAVEfmt ".utf8))
    number(UInt32(16)); number(UInt16(1)); number(UInt16(1))
    number(UInt32(16000)); number(UInt32(32000)); number(UInt16(2)); number(UInt16(16))
    data.append(Data("data".utf8)); number(UInt32(samples.count)); data.append(samples)
    return data
}

if CommandLine.arguments.count == 2 {
    switch CommandLine.arguments[1] {
    case "--check":
        guard Bundle.main.object(forInfoDictionaryKey: "NSMicrophoneUsageDescription") != nil else { fail("Missing microphone permission description.") }
        print("capri-mic ready"); exit(0)
    case "--list":
        do { FileHandle.standardOutput.write(try JSONEncoder().encode(inputDevices())); exit(0) }
        catch { fail("Couldn't list microphone inputs.") }
    case "--self-test":
        let data = wave(Data([0, 0, 255, 127, 0, 128]))
        guard data.count == 50, String(data: data.prefix(4), encoding: .utf8) == "RIFF", data[24] == 128, data[25] == 62, data[40] == 6 else { fail("Microphone WAV check failed.") }
        print("capri-mic PCM ready"); exit(0)
    default: break
    }
}
guard CommandLine.arguments.count == 2 || CommandLine.arguments.count == 4 && CommandLine.arguments[2] == "--device" else { fail("Missing recording path or invalid microphone option.") }
let path = CommandLine.arguments[1]
let requested = CommandLine.arguments.count == 4 ? CommandLine.arguments[3] : ""
let lock = NSLock()
var stopped = false
func shouldStop() -> Bool {
    lock.lock()
    defer { lock.unlock() }
    return stopped
}
DispatchQueue.global().async {
    _ = readLine()
    lock.lock(); stopped = true; lock.unlock()
}

var allowed = AVCaptureDevice.authorizationStatus(for: .audio) == .authorized
if AVCaptureDevice.authorizationStatus(for: .audio) == .notDetermined {
    var answered = false
    AVCaptureDevice.requestAccess(for: .audio) { granted in
        lock.lock(); allowed = granted; answered = true; lock.unlock()
    }
    while true {
        lock.lock(); let done = answered; lock.unlock()
        if done { break }
        if shouldStop() { exit(0) }
        RunLoop.current.run(until: Date(timeIntervalSinceNow: 0.05))
    }
}
guard allowed else { fail("Allow Capri under System Settings > Privacy & Security > Microphone, then hold TALK again.") }
if shouldStop() { exit(0) }
let devices = inputDevices()
guard let selected = requested.isEmpty ? devices.first(where: { $0.isDefault }) : devices.first(where: { $0.id == requested }) else {
    fail(requested.isEmpty ? "No microphone input is available. Open Capri Mainframe > Settings > Microphone input." : "Your selected microphone isn't connected. Choose another in Capri Mainframe > Settings > Microphone input.")
}

final class Recording {
    let gate = NSLock()
    var samples = Data()
    var accepting = true
    func receive(_ queue: AudioQueueRef, _ buffer: AudioQueueBufferRef) {
        gate.lock()
        let keep = accepting
        if keep {
            let bytes = min(Int(buffer.pointee.mAudioDataByteSize), max(0, 960000 - samples.count))
            samples.append(buffer.pointee.mAudioData.assumingMemoryBound(to: UInt8.self), count: bytes)
        }
        gate.unlock()
        if keep { AudioQueueEnqueueBuffer(queue, buffer, 0, nil) }
    }
}
let recording = Recording()
var format = AudioStreamBasicDescription(mSampleRate: 16000, mFormatID: kAudioFormatLinearPCM, mFormatFlags: kLinearPCMFormatFlagIsSignedInteger | kLinearPCMFormatFlagIsPacked, mBytesPerPacket: 2, mFramesPerPacket: 1, mBytesPerFrame: 2, mChannelsPerFrame: 1, mBitsPerChannel: 16, mReserved: 0)
var queue: AudioQueueRef?
let created = AudioQueueNewInput(&format, { context, queue, buffer, _, _, _ in
    guard let context = context else { return }
    Unmanaged<Recording>.fromOpaque(context).takeUnretainedValue().receive(queue, buffer)
}, Unmanaged.passUnretained(recording).toOpaque(), nil, nil, 0, &queue)
guard created == noErr, let queue = queue else { fail("Couldn't prepare microphone input (\(created)).") }
var uid: CFString = selected.id as CFString
let routed = withUnsafePointer(to: &uid) { AudioQueueSetProperty(queue, kAudioQueueProperty_CurrentDevice, $0, UInt32(MemoryLayout<CFString>.size)) }
guard routed == noErr else { AudioQueueDispose(queue, true); fail("Couldn't select \(selected.name) (\(routed)). Choose another microphone input.") }
for _ in 0..<3 {
    var buffer: AudioQueueBufferRef?
    guard AudioQueueAllocateBuffer(queue, 1600, &buffer) == noErr, let buffer = buffer, AudioQueueEnqueueBuffer(queue, buffer, 0, nil) == noErr else { AudioQueueDispose(queue, true); fail("Couldn't allocate microphone buffers.") }
}
if shouldStop() { AudioQueueDispose(queue, true); exit(0) }
let started = AudioQueueStart(queue, nil)
guard started == noErr else { AudioQueueDispose(queue, true); fail("Couldn't start \(selected.name) (\(started)). Check microphone access and choose another input if needed.") }
print("READY\t" + selected.name.replacingOccurrences(of: "\t", with: " ").replacingOccurrences(of: "\n", with: " "))
fflush(stdout)
let deadline = Date(timeIntervalSinceNow: 30)
while !shouldStop() && Date() < deadline {
    RunLoop.current.run(until: Date(timeIntervalSinceNow: 0.03))
}
recording.gate.lock(); recording.accepting = false; recording.gate.unlock()
AudioQueueStop(queue, true)
AudioQueueDispose(queue, true)
do {
    try wave(recording.samples).write(to: URL(fileURLWithPath: path))
    try FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: path)
} catch { fail("Microphone recording failed: \(error.localizedDescription)") }
