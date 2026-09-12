// Draws the macOS artwork that the build script consumes: the application icon and the background
// of the disk image window.
//
// Both are geometry rather than pictures, which is why they are drawn here instead of being kept as
// images somebody once exported. An icon drawn at every size is sharp at every size; an icon scaled
// up from one export is sharp at one. The same goes for the window background, which has to exist at
// two resolutions that must agree.
//
// Run it by hand when the artwork changes, and commit what it writes:
//
//   swift installer/make-art.swift
//
// It is deliberately not part of installer/build-macos.sh. A build that produces an installer should
// not also need a Swift compiler, and artwork changes on a different schedule from code.

import AppKit
import Foundation

// The mark: a ring with a dot at its centre, in the blue the interface uses for an active tunnel.
// The radii are fractions of the edge, so the same numbers describe the icon at 16 points and at
// 1024. They are the measurements of the icon this replaced, so nothing about the mark changes.
let markColour = NSColor(srgbRed: 59.0 / 255.0, green: 130.0 / 255.0, blue: 246.0 / 255.0, alpha: 1.0)
let outerRadius = 0.4609375
let ringInnerRadius = 0.30078125
let dotRadius = 0.1640625

// Written beside this file, so it does not matter which directory the command was run from.
let installerDirectory = URL(fileURLWithPath: #filePath).deletingLastPathComponent()

func output(_ name: String) -> URL {
    installerDirectory.appendingPathComponent(name)
}

/// Renders into a bitmap of the given pixel size and writes it as a PNG.
///
/// The point size is what a PNG records as its resolution, and it is what the Finder reads to decide
/// how large a window background is. Passing the pixel size for both gives the ordinary 72 dpi image
/// an icon needs; passing half gives the 144 dpi image a Retina background needs.
func writePng(pixels: NSSize, points: NSSize, to url: URL, draw: (CGContext) -> Void) {
    guard let bitmap = NSBitmapImageRep(
        bitmapDataPlanes: nil,
        pixelsWide: Int(pixels.width),
        pixelsHigh: Int(pixels.height),
        bitsPerSample: 8,
        samplesPerPixel: 4,
        hasAlpha: true,
        isPlanar: false,
        colorSpaceName: .deviceRGB,
        bytesPerRow: 0,
        bitsPerPixel: 0)
    else {
        FileHandle.standardError.write(Data("Could not create a bitmap for \(url.lastPathComponent).\n".utf8))
        exit(1)
    }

    bitmap.size = points

    let context = NSGraphicsContext(bitmapImageRep: bitmap)!
    NSGraphicsContext.saveGraphicsState()
    NSGraphicsContext.current = context

    // The context a representation gives out already maps points to pixels, so everything below is
    // drawn in points and the two resolutions need no separate set of coordinates.
    draw(context.cgContext)

    NSGraphicsContext.restoreGraphicsState()

    guard let data = bitmap.representation(using: .png, properties: [:]) else {
        FileHandle.standardError.write(Data("Could not encode \(url.lastPathComponent).\n".utf8))
        exit(1)
    }

    try! data.write(to: url)
}

/// Draws the mark filling a square of the given edge, centred, on transparency.
func drawMark(_ context: CGContext, edge: CGFloat) {
    let centre = CGPoint(x: edge / 2.0, y: edge / 2.0)

    func circle(_ radius: Double) -> CGRect {
        let r = edge * radius
        return CGRect(x: centre.x - r, y: centre.y - r, width: r * 2, height: r * 2)
    }

    context.setFillColor(markColour.cgColor)

    // The ring is one path with the hole wound the other way, so the even-odd rule leaves the middle
    // clear. Stroking a circle instead would put half the line outside the radius that was measured.
    let ring = CGMutablePath()
    ring.addEllipse(in: circle(outerRadius))
    ring.addEllipse(in: circle(ringInnerRadius))
    context.addPath(ring)
    context.fillPath(using: .evenOdd)

    context.fillEllipse(in: circle(dotRadius))
}

// MARK: the application icon

let iconset = URL(fileURLWithPath: NSTemporaryDirectory())
    .appendingPathComponent("OpenVpnPilot.iconset")

try? FileManager.default.removeItem(at: iconset)
try! FileManager.default.createDirectory(at: iconset, withIntermediateDirectories: true)

// The ten representations an icns is expected to carry. Anything missing is scaled by the system
// from whatever is nearest, which is the blurring this file exists to avoid.
for points in [16, 32, 128, 256, 512] {
    for scale in [1, 2] {
        let pixels = points * scale
        let name = scale == 1 ? "icon_\(points)x\(points).png" : "icon_\(points)x\(points)@2x.png"

        writePng(
            pixels: NSSize(width: pixels, height: pixels),
            points: NSSize(width: pixels, height: pixels),
            to: iconset.appendingPathComponent(name))
        { context in
            drawMark(context, edge: CGFloat(pixels))
        }
    }
}

let iconutil = Process()
iconutil.executableURL = URL(fileURLWithPath: "/usr/bin/iconutil")
iconutil.arguments = ["--convert", "icns", iconset.path, "--output", output("OpenVpnPilot.icns").path]
try! iconutil.run()
iconutil.waitUntilExit()

guard iconutil.terminationStatus == 0 else {
    FileHandle.standardError.write(Data("iconutil refused the icon set.\n".utf8))
    exit(1)
}

try? FileManager.default.removeItem(at: iconset)
print("wrote installer/OpenVpnPilot.icns")

// MARK: the disk image window background

// The size of the window the disk image opens at, in points. installer/build-macos.sh places the two
// icons at the positions named here, so the arrow drawn between them lands between them.
let windowWidth = 640.0
let windowHeight = 400.0
let applicationSlot = CGPoint(x: 170, y: 205)
let applicationsSlot = CGPoint(x: 470, y: 205)

func drawBackground(_ context: CGContext) {
    // The Finder does not redraw a disk image background for dark mode, so this is light and says so
    // rather than being a grey that looks broken under one of the two appearances.
    let top = NSColor(srgbRed: 0.976, green: 0.980, blue: 0.988, alpha: 1)
    let bottom = NSColor(srgbRed: 0.918, green: 0.929, blue: 0.949, alpha: 1)

    let gradient = CGGradient(
        colorsSpace: CGColorSpaceCreateDeviceRGB(),
        colors: [top.cgColor, bottom.cgColor] as CFArray,
        locations: [0, 1])!

    context.drawLinearGradient(
        gradient,
        start: CGPoint(x: 0, y: windowHeight),
        end: CGPoint(x: 0, y: 0),
        options: [])

    // Drawn from the top down, the way the positions below the icons are given to the Finder, so one
    // set of numbers describes the window rather than two that have to be kept in agreement.
    func fromTop(_ y: Double) -> Double { windowHeight - y }

    let ink = NSColor(srgbRed: 0.13, green: 0.15, blue: 0.18, alpha: 1)
    let faded = NSColor(srgbRed: 0.38, green: 0.42, blue: 0.47, alpha: 1)

    func centre(_ text: String, font: NSFont, colour: NSColor, atTop y: Double) {
        let paragraph = NSMutableParagraphStyle()
        paragraph.alignment = .center

        let attributed = NSAttributedString(
            string: text,
            attributes: [.font: font, .foregroundColor: colour, .paragraphStyle: paragraph])

        let height = attributed.size().height
        attributed.draw(in: CGRect(x: 0, y: fromTop(y) - height, width: windowWidth, height: height))
    }

    centre(
        "OpenVPN Pilot",
        font: .systemFont(ofSize: 22, weight: .semibold),
        colour: ink,
        atTop: 44)

    centre(
        "Drag the application onto the Applications folder.",
        font: .systemFont(ofSize: 13, weight: .regular),
        colour: faded,
        atTop: 76)

    // The helper is a second, separate installation and the window is the only place a person is
    // certain to look, so the window says so rather than leaving it to the release notes.
    centre(
        "Then install the helper package, which is what starts OpenVPN.",
        font: .systemFont(ofSize: 11, weight: .regular),
        colour: faded,
        atTop: 352)

    // The arrow sits between the two icon slots, clear of both by the width of half an icon.
    let arrowY = fromTop(applicationSlot.y)
    let arrowFrom = applicationSlot.x + 92
    let arrowTo = applicationsSlot.x - 92

    context.setStrokeColor(faded.withAlphaComponent(0.55).cgColor)
    context.setLineWidth(2)
    context.setLineCap(.round)
    context.setLineJoin(.round)

    context.move(to: CGPoint(x: arrowFrom, y: arrowY))
    context.addLine(to: CGPoint(x: arrowTo - 9, y: arrowY))
    context.strokePath()

    context.move(to: CGPoint(x: arrowTo - 18, y: arrowY + 8))
    context.addLine(to: CGPoint(x: arrowTo, y: arrowY))
    context.addLine(to: CGPoint(x: arrowTo - 18, y: arrowY - 8))
    context.strokePath()
}

// One file at twice the resolution, recorded as being half its pixel size. The Finder reads that and
// lays the window out in points, so the background is sharp on a Retina display and correct on any
// other, without the two versions a pair of files would have to be kept in step.
writePng(
    pixels: NSSize(width: windowWidth * 2, height: windowHeight * 2),
    points: NSSize(width: windowWidth, height: windowHeight),
    to: output("dmg-background.png"),
    draw: drawBackground)

print("wrote installer/dmg-background.png")
