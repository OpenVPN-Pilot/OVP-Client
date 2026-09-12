// Draws everything visual the product is made of, into assets/artwork.
//
// One mark, one place it comes from, four things that need it: the macOS application icon, the
// Windows application icon, the menu bar template on macOS, and the background of the disk image
// window. All of it is geometry rather than pictures, because an icon drawn at every size is sharp
// at every size and an icon scaled up from one export is sharp at one.
//
// Run it by hand when the artwork changes, and commit what it writes:
//
//   swift assets/make-artwork.swift
//
// It is deliberately not part of any build. Producing an installer should not also need a Swift
// compiler, and artwork changes on a different schedule from code.

import AppKit
import Foundation

// MARK: the mark

// The ring with a dot at its centre, in the blue the interface uses for an active tunnel. The radii
// are fractions of the edge, so the same numbers describe it at 16 points and at 1024.
let markColour = NSColor(srgbRed: 59.0 / 255.0, green: 130.0 / 255.0, blue: 246.0 / 255.0, alpha: 1)
let outerRadius = 0.4609375
let ringInnerRadius = 0.30078125
let dotRadius = 0.1640625

// The menu bar wants more air around it than an application icon does, and it is the one place the
// mark is drawn at a fixed small size, so it carries its own proportions.
let templateOuterRadius = 0.4166666666666667
let templateRingInnerRadius = 0.2777777777777778
let templateDotRadius = 0.16666666666666666

// Apple's grid: the body of an icon fills 824 of 1024 with a corner radius of 185.4. The same shape
// is used for Windows, so the product looks like one product on both.
let bodyFraction = 824.0 / 1024.0
let cornerFraction = 185.4 / 824.0

// How much of the body the mark takes up inside it. Smaller than the body, because a mark that
// reaches the rounded corners reads as a sticker rather than as an icon.
let markInTileFraction = 0.78

/// The edge below which the tile is dropped and the mark is drawn on its own.
///
/// At 16 points a tile and a mark inside it leave the mark about ten points across, and the dot in
/// its middle two. What survives at that size is the mark filling the square, which is what the
/// shell shows in a list, a menu and a title bar.
let smallestTile = 32.0

/// Fills the mark, optionally scaled down, at the centre of a square of the given edge.
func fillMark(
    _ context: CGContext,
    edge: CGFloat,
    colour: NSColor,
    scale: Double = 1.0,
    outer: Double = outerRadius,
    inner: Double = ringInnerRadius,
    dot: Double = dotRadius)
{
    let centre = CGPoint(x: edge / 2, y: edge / 2)

    func circle(_ radius: Double) -> CGRect {
        let r = edge * radius * scale
        return CGRect(x: centre.x - r, y: centre.y - r, width: r * 2, height: r * 2)
    }

    context.setFillColor(colour.cgColor)

    // The ring is the outer circle and the hole in one path under the even-odd rule, so the middle
    // stays clear. Stroking a circle instead would put half the line outside the measured radius.
    let ring = CGMutablePath()
    ring.addEllipse(in: circle(outer))
    ring.addEllipse(in: circle(inner))
    context.addPath(ring)
    context.fillPath(using: .evenOdd)

    // The dot separately, because adding it to the same path would cancel the hole it sits in.
    context.fillEllipse(in: circle(dot))
}

/// Draws the application icon: the mark on a light tile, or on its own when there is no room for a
/// tile.
///
/// The edge is in pixels and the tile is decided by the size the icon is shown at, which are not the
/// same number on a Retina display: a 16 point icon is 32 pixels and is still a 16 point icon.
func drawApplicationIcon(_ context: CGContext, edge: CGFloat, shownAt points: CGFloat? = nil) {
    guard (points ?? edge) >= smallestTile else {
        fillMark(context, edge: edge, colour: markColour)
        return
    }

    let body = edge * bodyFraction
    let rect = CGRect(x: (edge - body) / 2, y: (edge - body) / 2, width: body, height: body)
    let corner = body * cornerFraction
    let tile = CGPath(roundedRect: rect, cornerWidth: corner, cornerHeight: corner, transform: nil)

    // The shadow is what separates a light tile from a light background. It is drawn under an opaque
    // fill first, so the gradient that follows is not drawn through it.
    context.saveGState()
    context.setShadow(
        offset: CGSize(width: 0, height: -edge * 0.008),
        blur: edge * 0.022,
        color: NSColor(white: 0, alpha: 0.22).cgColor)
    context.addPath(tile)
    context.setFillColor(NSColor.white.cgColor)
    context.fillPath()
    context.restoreGState()

    context.saveGState()
    context.addPath(tile)
    context.clip()

    let gradient = CGGradient(
        colorsSpace: CGColorSpaceCreateDeviceRGB(),
        colors: [NSColor(white: 0.96, alpha: 1).cgColor,
                 NSColor(white: 0.86, alpha: 1).cgColor] as CFArray,
        locations: [0, 1])!

    context.drawLinearGradient(
        gradient,
        start: CGPoint(x: 0, y: rect.maxY),
        end: CGPoint(x: 0, y: rect.minY),
        options: [])

    context.restoreGState()

    fillMark(context, edge: edge, colour: markColour, scale: bodyFraction * markInTileFraction)
}

/// Draws the menu bar template: black shapes on transparency, which the menu bar tints itself.
func drawTemplate(_ context: CGContext, edge: CGFloat) {
    fillMark(
        context,
        edge: edge,
        colour: .black,
        outer: templateOuterRadius,
        inner: templateRingInnerRadius,
        dot: templateDotRadius)
}

// MARK: rendering

let artwork = URL(fileURLWithPath: #filePath)
    .deletingLastPathComponent()
    .appendingPathComponent("artwork")

try! FileManager.default.createDirectory(at: artwork, withIntermediateDirectories: true)

/// Renders into a bitmap and returns it.
///
/// The point size is what a PNG records as its resolution, and it is what the Finder reads to decide
/// how large a window background is. Passing the pixel size for both gives the ordinary 72 dpi image
/// an icon needs; passing half gives the 144 dpi image a Retina background needs.
func render(pixels: NSSize, points: NSSize, draw: (CGContext) -> Void) -> NSBitmapImageRep {
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
        FileHandle.standardError.write(Data("Could not create a bitmap.\n".utf8))
        exit(1)
    }

    bitmap.size = points

    let context = NSGraphicsContext(bitmapImageRep: bitmap)!
    NSGraphicsContext.saveGraphicsState()
    NSGraphicsContext.current = context

    // The context a representation gives out already maps points to pixels, so everything is drawn
    // in points and the two resolutions need no separate set of coordinates.
    draw(context.cgContext)

    NSGraphicsContext.restoreGraphicsState()
    return bitmap
}

func png(_ bitmap: NSBitmapImageRep) -> Data {
    guard let data = bitmap.representation(using: .png, properties: [:]) else {
        FileHandle.standardError.write(Data("Could not encode a PNG.\n".utf8))
        exit(1)
    }

    return data
}

func square(_ edge: Int, draw: @escaping (CGContext) -> Void) -> NSBitmapImageRep {
    render(
        pixels: NSSize(width: edge, height: edge),
        points: NSSize(width: edge, height: edge))
    { context in
        draw(context)
    }
}

// MARK: the macOS application icon

let iconset = URL(fileURLWithPath: NSTemporaryDirectory()).appendingPathComponent("OpenVpnPilot.iconset")
try? FileManager.default.removeItem(at: iconset)
try! FileManager.default.createDirectory(at: iconset, withIntermediateDirectories: true)

// The ten representations an icns is expected to carry. Anything missing is scaled by the system
// from whatever is nearest, which is the blurring this file exists to avoid.
for points in [16, 32, 128, 256, 512] {
    for scale in [1, 2] {
        let pixels = points * scale
        let name = scale == 1 ? "icon_\(points)x\(points).png" : "icon_\(points)x\(points)@2x.png"

        let data = png(square(pixels) {
            drawApplicationIcon($0, edge: CGFloat(pixels), shownAt: CGFloat(points))
        })
        try! data.write(to: iconset.appendingPathComponent(name))
    }
}

let iconutil = Process()
iconutil.executableURL = URL(fileURLWithPath: "/usr/bin/iconutil")
iconutil.arguments = [
    "--convert", "icns", iconset.path,
    "--output", artwork.appendingPathComponent("OpenVpnPilot.icns").path,
]
try! iconutil.run()
iconutil.waitUntilExit()

guard iconutil.terminationStatus == 0 else {
    FileHandle.standardError.write(Data("iconutil refused the icon set.\n".utf8))
    exit(1)
}

try? FileManager.default.removeItem(at: iconset)
print("wrote assets/artwork/OpenVpnPilot.icns")

// MARK: the Windows application icon

// An ICO is a small header, one directory entry per size, and the images one after another. Since
// Windows Vista an entry may be a PNG rather than a bitmap, which is what every size here is: it
// keeps the alpha channel and the file small enough to embed in the executable.
func ico(_ sizes: [Int], draw: @escaping (CGContext, CGFloat) -> Void) -> Data {
    let images = sizes.map { size in
        png(square(size) { context in draw(context, CGFloat(size)) })
    }

    var file = Data()
    var directory = Data()
    var offset = 6 + sizes.count * 16

    func append16(_ value: Int, to data: inout Data) {
        data.append(UInt8(value & 0xFF))
        data.append(UInt8((value >> 8) & 0xFF))
    }

    func append32(_ value: Int, to data: inout Data) {
        append16(value & 0xFFFF, to: &data)
        append16((value >> 16) & 0xFFFF, to: &data)
    }

    append16(0, to: &file)               // reserved
    append16(1, to: &file)               // an icon rather than a cursor
    append16(sizes.count, to: &file)

    for (index, size) in sizes.enumerated() {
        // 256 is written as zero, which is the only way the one byte each dimension gets can say it.
        directory.append(UInt8(size == 256 ? 0 : size))
        directory.append(UInt8(size == 256 ? 0 : size))
        directory.append(0)              // colours in the palette: none, this is true colour
        directory.append(0)              // reserved
        append16(1, to: &directory)      // planes
        append16(32, to: &directory)     // bits per pixel
        append32(images[index].count, to: &directory)
        append32(offset, to: &directory)

        offset += images[index].count
    }

    file.append(directory)

    for image in images {
        file.append(image)
    }

    return file
}

let windowsIcon = ico([16, 20, 24, 32, 48, 64, 128, 256]) { context, edge in
    drawApplicationIcon(context, edge: edge)
}

try! windowsIcon.write(to: artwork.appendingPathComponent("OpenVpnPilot.ico"))
print("wrote assets/artwork/OpenVpnPilot.ico")

// MARK: the menu bar template

// Eighteen points, which is what the menu bar gives an item, at twice the resolution.
let template = render(
    pixels: NSSize(width: 36, height: 36),
    points: NSSize(width: 36, height: 36))
{ context in
    drawTemplate(context, edge: 36)
}

try! png(template).write(to: artwork.appendingPathComponent("status-item.png"))
print("wrote assets/artwork/status-item.png")

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

    // Measured from the top down, the way the positions below the icons are given to the Finder, so
    // one set of numbers describes the window rather than two that have to be kept in agreement.
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

    centre("OpenVPN Pilot", font: .systemFont(ofSize: 22, weight: .semibold), colour: ink, atTop: 44)

    centre(
        "Drag the application onto the Applications folder.",
        font: .systemFont(ofSize: 13, weight: .regular),
        colour: faded,
        atTop: 76)

    // The helper is a second, separate installation and this window is the only place a person is
    // certain to look, so it says so rather than leaving it to the release notes.
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
let background = render(
    pixels: NSSize(width: windowWidth * 2, height: windowHeight * 2),
    points: NSSize(width: windowWidth, height: windowHeight),
    draw: drawBackground)

try! png(background).write(to: artwork.appendingPathComponent("dmg-background.png"))
print("wrote assets/artwork/dmg-background.png")
