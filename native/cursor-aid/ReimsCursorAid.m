#import <AppKit/AppKit.h>
#import <CoreGraphics/CoreGraphics.h>
#import <ImageIO/ImageIO.h>
#include <arpa/inet.h>
#include <errno.h>
#include <fcntl.h>
#include <mach-o/dyld.h>
#include <math.h>
#include <signal.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>

static const NSUInteger DataLimit = 4 * 1024 * 1024;
static const NSUInteger WireLimit = 6 * 1024 * 1024;
static const NSUInteger ReadBudget = 256 * 1024;
static volatile sig_atomic_t StopRequested;

typedef struct { BOOL selfTest, glyphWorker; double duration; const char *moveTarget; } Options;
typedef struct { CGDirectDisplayID identifier; BOOL main, mirrored; CGRect bounds; } Display;

static BOOL ParseOptions(int argc, const char *const argv[], Options *out) {
    *out = (Options){0};
    if (argc == 1) return YES;
    if (argc == 2 && !strcmp(argv[1], "--self-test")) {
        out->selfTest = YES;
        return YES;
    }
    if (argc == 2 && !strcmp(argv[1], "--glyph-worker")) {
        out->glyphWorker = YES;
        return YES;
    }
    if (argc == 3 && !strcmp(argv[1], "--move-to-display")) {
        if (strcmp(argv[2], "main") && strcmp(argv[2], "extended")) return NO;
        out->moveTarget = argv[2];
        return YES;
    }
    if (argc != 3 || strcmp(argv[1], "--duration")) return NO;
    char *end = NULL;
    errno = 0;
    double value = strtod(argv[2], &end);
    if (errno || end == argv[2] || *end || !isfinite(value) || value <= 0) return NO;
    out->duration = value;
    return YES;
}

static double Now(void) {
    struct timespec value;
    if (clock_gettime(CLOCK_MONOTONIC, &value)) abort();
    return value.tv_sec + value.tv_nsec / 1e9;
}

static BOOL ValidSize(NSSize size) {
    return isfinite(size.width) && isfinite(size.height) && size.width > 0 && size.height > 0;
}

static BOOL ValidPoint(NSPoint point) { return isfinite(point.x) && isfinite(point.y); }

static BOOL BoundedData(size_t width, size_t height, size_t stride, NSUInteger length) {
    return width && height && width <= 4096 && height <= 4096 && stride &&
        stride <= DataLimit / height && length && length <= DataLimit;
}

static NSRect CursorFrame(NSPoint mouse, NSSize size, NSPoint hotSpot) {
    // NSCursor's hot spot starts at the image's top left; AppKit screens start at the bottom left.
    return NSMakeRect(mouse.x - hotSpot.x, mouse.y - size.height + hotSpot.y, size.width, size.height);
}

static BOOL NeedsContentDisplay(BOOL glyphChanged, BOOL shown, NSRect previous, NSRect frame) {
    return glyphChanged || !shown || !NSEqualSizes(previous.size, frame.size);
}

static BOOL DisplayCenter(CGRect bounds, CGPoint *point) {
    if (!isfinite(bounds.origin.x) || !isfinite(bounds.origin.y) ||
        !isfinite(bounds.size.width) || !isfinite(bounds.size.height) ||
        bounds.size.width <= 0 || bounds.size.height <= 0) return NO;
    *point = CGPointMake(CGRectGetMidX(bounds), CGRectGetMidY(bounds));
    return isfinite(point->x) && isfinite(point->y);
}

static const Display *SelectDisplay(const Display *displays, size_t count,
                                    const char *target, NSUInteger *candidates) {
    *candidates = 0;
    const Display *selected = NULL;
    for (size_t i = 0; i < count; i++) {
        BOOL matches = !strcmp(target, "main") ? displays[i].main :
            (!displays[i].main && !displays[i].mirrored);
        if (displays[i].identifier && matches) { selected = &displays[i]; (*candidates)++; }
    }
    return *candidates == 1 ? selected : NULL;
}

static void EmitJSON(NSDictionary *value) {
    NSData *data = [NSJSONSerialization dataWithJSONObject:value options:0 error:NULL];
    if (data) { fwrite(data.bytes, 1, data.length, stdout); fputc('\n', stdout); }
}

static int MoveToDisplay(const char *target) {
    NSMutableDictionary *receipt = [@{@"schema":@1, @"action":@"move-to-display",
        @"target":[NSString stringWithUTF8String:target], @"ok":@NO, @"cgError":NSNull.null,
        @"displayID":NSNull.null, @"boundsCG":NSNull.null, @"positionCG":NSNull.null,
        @"candidateCount":@0, @"error":NSNull.null} mutableCopy];
    CGDirectDisplayID identifiers[64];
    uint32_t count = 0;
    CGError error = CGGetActiveDisplayList(64, identifiers, &count);
    // A full bounded buffer cannot prove unique selection; refuse rather than guess.
    if (error || !count || count >= 64) {
        receipt[@"error"] = @"display-query-failed";
        if (error) receipt[@"cgError"] = @(error);
        EmitJSON(receipt); return 1;
    }
    Display displays[64];
    CGDirectDisplayID main = CGMainDisplayID();
    for (uint32_t i = 0; i < count; i++) {
        displays[i] = (Display){identifiers[i], identifiers[i] == main,
            CGDisplayIsInMirrorSet(identifiers[i]), CGDisplayBounds(identifiers[i])};
    }
    NSUInteger candidates = 0;
    const Display *display = SelectDisplay(displays, count, target, &candidates);
    receipt[@"candidateCount"] = @(candidates);
    if (!display) {
        receipt[@"error"] = !strcmp(target, "main") ? @"no-main-display" :
            (candidates ? @"ambiguous-extended-display" : @"no-extended-display");
        EmitJSON(receipt); return 1;
    }
    CGPoint center;
    if (!DisplayCenter(display->bounds, &center)) {
        receipt[@"error"] = @"invalid-bounds";
        EmitJSON(receipt); return 1;
    }
    CGRect bounds = display->bounds;
    receipt[@"displayID"] = @(display->identifier);
    receipt[@"boundsCG"] = @{@"x":@(bounds.origin.x), @"y":@(bounds.origin.y),
        @"width":@(bounds.size.width), @"height":@(bounds.size.height)};
    receipt[@"positionCG"] = @{@"x":@(center.x), @"y":@(center.y)};
    error = CGWarpMouseCursorPosition(center);
    receipt[@"cgError"] = @(error);
    receipt[@"ok"] = error == kCGErrorSuccess ? @YES : @NO;
    receipt[@"error"] = error == kCGErrorSuccess ? NSNull.null : @"warp-failed";
    EmitJSON(receipt);
    return error == kCGErrorSuccess ? 0 : 1;
}

static BOOL SameBytes(NSData *a, NSSize aSize, NSData *b, NSSize bSize) {
    return a && b && NSEqualSizes(aSize, bSize) && [a isEqualToData:b];
}

@interface CursorGlyph : NSObject
@property(nonatomic, strong) NSImage *image;
@property(nonatomic, strong) NSData *data;
@property(nonatomic, assign) CGImageRef raster;
@property(nonatomic) NSSize size;
@property(nonatomic) NSPoint hotSpot;
@end

@implementation CursorGlyph
- (void)dealloc { if (_raster) CGImageRelease(_raster); }
@end

static CursorGlyph *ReadGlyph(void) {
    NSCursor *cursor = NSCursor.currentSystemCursor;
    NSImage *image = cursor.image;
    if (!image || !ValidSize(image.size) || !ValidPoint(cursor.hotSpot)) return nil;
    NSRect proposed = NSMakeRect(0, 0, image.size.width, image.size.height);
    CGImageRef raster = [image CGImageForProposedRect:&proposed context:nil hints:nil];
    if (!raster) return nil;
    size_t width = CGImageGetWidth(raster), height = CGImageGetHeight(raster);
    size_t stride = CGImageGetBytesPerRow(raster);
    if (!BoundedData(width, height, stride, 1)) return nil;
    CGDataProviderRef provider = CGImageGetDataProvider(raster);
    if (!provider) return nil;
    NSData *data = CFBridgingRelease(CGDataProviderCopyData(provider));
    if (!BoundedData(width, height, stride, data.length)) return nil;
    CursorGlyph *glyph = [CursorGlyph new];
    glyph.image = image;
    glyph.data = data;
    glyph.raster = CGImageRetain(raster);
    glyph.size = image.size;
    glyph.hotSpot = cursor.hotSpot;
    return glyph;
}

static BOOL SameGlyph(CursorGlyph *a, CursorGlyph *b) {
    if (!a || !b || !SameBytes(a.data, a.size, b.data, b.size)) return NO;
    CGImageRef x = a.raster, y = b.raster;
    CGColorSpaceRef cx = CGImageGetColorSpace(x), cy = CGImageGetColorSpace(y);
    // A custom decode table declines the cache hit; it never substitutes a guessed image.
    return !CGImageGetDecode(x) && !CGImageGetDecode(y) &&
        CGImageGetWidth(x) == CGImageGetWidth(y) && CGImageGetHeight(x) == CGImageGetHeight(y) &&
        CGImageGetBitsPerPixel(x) == CGImageGetBitsPerPixel(y) &&
        CGImageGetBitsPerComponent(x) == CGImageGetBitsPerComponent(y) &&
        CGImageGetBytesPerRow(x) == CGImageGetBytesPerRow(y) &&
        CGImageGetBitmapInfo(x) == CGImageGetBitmapInfo(y) &&
        CGImageGetRenderingIntent(x) == CGImageGetRenderingIntent(y) &&
        CGImageGetShouldInterpolate(x) == CGImageGetShouldInterpolate(y) &&
        CGImageIsMask(x) == CGImageIsMask(y) && ((!cx && !cy) || (cx && cy && CFEqual(cx, cy)));
}

static BOOL SameSnapshot(CursorGlyph *a, CursorGlyph *b) {
    return (!a && !b) || (SameGlyph(a, b) && NSEqualPoints(a.hotSpot, b.hotSpot));
}

static BOOL Number(id value, double *result) {
    if (![value isKindOfClass:NSNumber.class] || CFGetTypeID((__bridge CFTypeRef)value) == CFBooleanGetTypeID()) return NO;
    *result = [value doubleValue];
    return isfinite(*result);
}

static BOOL PositiveInteger(id value, NSUInteger maximum, NSUInteger *result) {
    double number;
    if (!Number(value, &number) || number < 1 || number > maximum || floor(number) != number) return NO;
    *result = (NSUInteger)number;
    return YES;
}

static BOOL PNGPropertiesValid(NSDictionary *properties, NSUInteger width, NSUInteger height) {
    NSUInteger w, h, depth;
    if (!PositiveInteger(properties[(__bridge NSString *)kCGImagePropertyPixelWidth], 4096, &w) ||
        !PositiveInteger(properties[(__bridge NSString *)kCGImagePropertyPixelHeight], 4096, &h) ||
        !PositiveInteger(properties[(__bridge NSString *)kCGImagePropertyDepth], 16, &depth)) return NO;
    if (w != width || h != height || (depth != 1 && depth != 2 && depth != 4 && depth != 8 && depth != 16)) return NO;
    // PNG expands to at most four channels; check before asking AppKit to decode pixels.
    return w <= DataLimit / h / (depth > 8 ? 8 : 4);
}

static BOOL PNGPreflight(NSData *png, NSUInteger width, NSUInteger height) {
    if (!png.length || png.length > DataLimit) return NO;
    NSDictionary *options = @{(__bridge NSString *)kCGImageSourceShouldCache:@NO};
    CGImageSourceRef source = CGImageSourceCreateWithData((__bridge CFDataRef)png, (__bridge CFDictionaryRef)options);
    if (!source) return NO;
    CFStringRef type = CGImageSourceGetType(source);
    BOOL valid = type && CFEqual(type, CFSTR("public.png")) && CGImageSourceGetCount(source) == 1;
    NSDictionary *properties = valid ? CFBridgingRelease(CGImageSourceCopyPropertiesAtIndex(source, 0, (__bridge CFDictionaryRef)options)) : nil;
    valid = valid && PNGPropertiesValid(properties, width, height);
    CFRelease(source);
    return valid;
}

static NSData *EncodeGlyph(CursorGlyph *glyph) {
    NSDictionary *packet;
    if (!glyph) {
        packet = @{@"schema":@1, @"available":@NO, @"sizePoints":NSNull.null,
            @"hotSpot":NSNull.null, @"pixelSize":NSNull.null, @"png":NSNull.null};
    } else {
        NSBitmapImageRep *rep = [[NSBitmapImageRep alloc] initWithCGImage:glyph.raster];
        NSData *png = [rep representationUsingType:NSBitmapImageFileTypePNG properties:@{}];
        NSUInteger width = CGImageGetWidth(glyph.raster), height = CGImageGetHeight(glyph.raster);
        if (!PNGPreflight(png, width, height)) return nil;
        packet = @{@"schema":@1, @"available":@YES,
            @"sizePoints":@{@"width":@(glyph.size.width), @"height":@(glyph.size.height)},
            @"hotSpot":@{@"x":@(glyph.hotSpot.x), @"y":@(glyph.hotSpot.y)},
            @"pixelSize":@{@"width":@(width), @"height":@(height)}, @"png":[png base64EncodedStringWithOptions:0]};
    }
    NSData *data = [NSJSONSerialization dataWithJSONObject:packet options:0 error:NULL];
    return data.length && data.length <= WireLimit ? data : nil;
}

static BOOL DecodeGlyph(NSData *packet, CursorGlyph **out) {
    *out = nil;
    if (!packet.length || packet.length > WireLimit) return NO;
    id value = [NSJSONSerialization JSONObjectWithData:packet options:0 error:NULL];
    if (![value isKindOfClass:NSDictionary.class] || [value count] != 6) return NO;
    NSDictionary *object = value;
    NSUInteger schema;
    id available = object[@"available"];
    if (!PositiveInteger(object[@"schema"], 1, &schema) || ![available isKindOfClass:NSNumber.class] ||
        CFGetTypeID((__bridge CFTypeRef)available) != CFBooleanGetTypeID()) return NO;
    if (![available boolValue]) {
        return object[@"sizePoints"] == NSNull.null && object[@"hotSpot"] == NSNull.null &&
            object[@"pixelSize"] == NSNull.null && object[@"png"] == NSNull.null;
    }
    id size = object[@"sizePoints"], hot = object[@"hotSpot"], pixels = object[@"pixelSize"], encoded = object[@"png"];
    if (![size isKindOfClass:NSDictionary.class] || [size count] != 2 ||
        ![hot isKindOfClass:NSDictionary.class] || [hot count] != 2 ||
        ![pixels isKindOfClass:NSDictionary.class] || [pixels count] != 2 ||
        ![encoded isKindOfClass:NSString.class] || [encoded length] > ((DataLimit + 2) / 3) * 4) return NO;
    double width, height, x, y;
    NSUInteger pixelWidth, pixelHeight;
    if (!Number(size[@"width"], &width) || !Number(size[@"height"], &height) || width <= 0 || height <= 0 ||
        !Number(hot[@"x"], &x) || !Number(hot[@"y"], &y) ||
        !PositiveInteger(pixels[@"width"], 4096, &pixelWidth) || !PositiveInteger(pixels[@"height"], 4096, &pixelHeight)) return NO;
    NSData *png = [[NSData alloc] initWithBase64EncodedString:encoded options:0];
    if (!PNGPreflight(png, pixelWidth, pixelHeight)) return NO;
    NSBitmapImageRep *rep = [[NSBitmapImageRep alloc] initWithData:png];
    if (!rep || rep.pixelsWide != (NSInteger)pixelWidth || rep.pixelsHigh != (NSInteger)pixelHeight ||
        rep.bytesPerRow <= 0 || (NSUInteger)rep.bytesPerRow > DataLimit / pixelHeight) return NO;
    CursorGlyph *glyph = [CursorGlyph new];
    glyph.size = NSMakeSize(width, height);
    glyph.hotSpot = NSMakePoint(x, y);
    glyph.data = png;
    glyph.image = [[NSImage alloc] initWithSize:glyph.size];
    [glyph.image addRepresentation:rep];
    *out = glyph;
    return YES;
}

static BOOL ExtractLatest(NSMutableData *buffer, NSData **latest) {
    if (buffer.length > WireLimit + sizeof(uint32_t)) return NO;
    while (buffer.length >= sizeof(uint32_t)) {
        uint32_t prefix;
        memcpy(&prefix, buffer.bytes, sizeof(prefix));
        NSUInteger length = ntohl(prefix);
        if (!length || length > WireLimit) return NO;
        if (buffer.length - sizeof(prefix) < length) break;
        *latest = [buffer subdataWithRange:NSMakeRange(sizeof(prefix), length)];
        [buffer replaceBytesInRange:NSMakeRange(0, sizeof(prefix) + length) withBytes:NULL length:0];
    }
    return YES;
}

static BOOL ReadLatest(int descriptor, NSMutableData *buffer, NSData **latest) {
    *latest = nil;
    if (buffer.length > WireLimit + sizeof(uint32_t)) return NO;
    NSUInteger budget = ReadBudget;
    while (budget) {
        unsigned char bytes[16384];
        NSUInteger capacity = WireLimit + sizeof(uint32_t) - buffer.length;
        if (!capacity) return NO;
        ssize_t count = read(descriptor, bytes, MIN(sizeof(bytes), MIN(budget, capacity)));
        if (count > 0) {
            [buffer appendBytes:bytes length:(NSUInteger)count];
            budget -= (NSUInteger)count;
            if (!ExtractLatest(buffer, latest)) return NO;
        } else if (count == 0) return NO;
        else if (errno == EAGAIN || errno == EWOULDBLOCK) break;
        else if (errno != EINTR) return NO;
        else if (StopRequested) break;
    }
    return YES;
}

static BOOL WriteAll(const void *bytes, NSUInteger length) {
    const unsigned char *next = bytes;
    while (length) {
        ssize_t written = write(STDOUT_FILENO, next, length);
        if (written > 0) { next += written; length -= (NSUInteger)written; }
        else if (written == 0 || errno != EINTR) return NO;
    }
    return YES;
}

static int RunGlyphWorker(void) {
    // This exec'd child's main thread alone owns NSCursor and its mutable image cache.
    signal(SIGTERM, SIG_DFL);
    signal(SIGINT, SIG_DFL);
    signal(SIGPIPE, SIG_DFL);
    [NSApplication.sharedApplication setActivationPolicy:NSApplicationActivationPolicyAccessory];
    CursorGlyph *previous = nil;
    BOOL sent = NO;
    double next = Now();
    for (;;) { @autoreleasepool {
        CursorGlyph *fresh = ReadGlyph();
        BOOL same = sent && SameSnapshot(previous, fresh);
        if (!same) {
            NSData *packet = EncodeGlyph(fresh);
            if (!packet) { fprintf(stderr, "Cannot encode cursor snapshot\n"); return 1; }
            uint32_t prefix = htonl((uint32_t)packet.length);
            if (!WriteAll(&prefix, sizeof(prefix)) || !WriteAll(packet.bytes, packet.length)) return 1;
            sent = YES;
        }
        previous = fresh;
        double now = Now();
        do { next += 0.1; } while (next <= now);
        [NSThread sleepForTimeInterval:next - now];
    }}
}

@interface GlyphWorker : NSObject
@property(nonatomic, strong) NSTask *task;
@property(nonatomic, strong) NSFileHandle *input;
@property(nonatomic, strong) NSMutableData *buffer;
@end
@implementation GlyphWorker
@end

static GlyphWorker *StartGlyphWorker(void) {
    uint32_t length = 0;
    _NSGetExecutablePath(NULL, &length);
    if (!length || length > 65536) return nil;
    NSMutableData *path = [NSMutableData dataWithLength:length];
    if (_NSGetExecutablePath(path.mutableBytes, &length)) return nil;
    NSString *executable = [NSFileManager.defaultManager stringWithFileSystemRepresentation:path.bytes length:strlen(path.bytes)];
    if (!executable) return nil;
    NSPipe *pipe = NSPipe.pipe;
    int descriptor = pipe.fileHandleForReading.fileDescriptor;
    int flags = fcntl(descriptor, F_GETFL);
    if (flags == -1 || fcntl(descriptor, F_SETFL, flags | O_NONBLOCK) == -1) return nil;
    NSTask *task = [NSTask new];
    task.executableURL = [NSURL fileURLWithPath:executable];
    task.arguments = @[@"--glyph-worker"];
    task.standardInput = NSFileHandle.fileHandleWithNullDevice;
    task.standardOutput = pipe;
    NSError *error;
    if (![task launchAndReturnError:&error]) { fprintf(stderr, "%s\n", error.localizedDescription.UTF8String); return nil; }
    [pipe.fileHandleForWriting closeFile];
    GlyphWorker *worker = [GlyphWorker new];
    worker.task = task;
    worker.input = pipe.fileHandleForReading;
    worker.buffer = [NSMutableData new];
    return worker;
}

static int SelfTest(void) {
    NSUInteger checks = 0, failures = 0;
#define CHECK(value, label) do { checks++; if (!(value)) { failures++; fprintf(stderr, "%s\n", label); } } while (0)
    Options options;
    const char *normal[] = {"aid"}, *bounded[] = {"aid", "--duration", "120"};
    const char *test[] = {"aid", "--self-test"}, *missing[] = {"aid", "--duration"};
    const char *invalid[] = {"aid", "--duration", "nan"}, *zero[] = {"aid", "--duration", "0"};
    const char *negative[] = {"aid", "--duration", "-1"}, *trailing[] = {"aid", "--duration", "1x"};
    const char *overflow[] = {"aid", "--duration", "1e999"}, *unknown[] = {"aid", "--unknown"};
    const char *moveMain[] = {"aid", "--move-to-display", "main"};
    const char *moveExtended[] = {"aid", "--move-to-display", "extended"};
    const char *moveInvalid[] = {"aid", "--move-to-display", "other"};
    const char *moveMissing[] = {"aid", "--move-to-display"};
    CHECK(ParseOptions(1, normal, &options) && !options.selfTest && options.duration == 0, "persistent arguments");
    CHECK(ParseOptions(3, bounded, &options) && options.duration == 120, "bounded arguments");
    CHECK(ParseOptions(2, test, &options) && options.selfTest, "self-test arguments");
    CHECK(!ParseOptions(2, missing, &options), "missing duration");
    CHECK(!ParseOptions(3, invalid, &options), "nonfinite duration");
    CHECK(!ParseOptions(3, zero, &options), "zero duration");
    CHECK(!ParseOptions(3, negative, &options), "negative duration");
    CHECK(!ParseOptions(3, trailing, &options), "trailing duration data");
    CHECK(!ParseOptions(3, overflow, &options), "overflowed duration");
    CHECK(!ParseOptions(2, unknown, &options), "unknown arguments");
    CHECK(ParseOptions(3, moveMain, &options) && !strcmp(options.moveTarget, "main"), "main move arguments");
    CHECK(ParseOptions(3, moveExtended, &options) && !strcmp(options.moveTarget, "extended"), "extended move arguments");
    CHECK(!ParseOptions(3, moveInvalid, &options), "invalid display target");
    CHECK(!ParseOptions(2, moveMissing, &options), "missing display target");
    CHECK(NSEqualRects(CursorFrame(NSMakePoint(100,100), NSMakeSize(28,40), NSMakePoint(5,5)), NSMakeRect(95,65,28,40)), "top-left hotspot");
    CHECK(NSEqualRects(CursorFrame(NSMakePoint(-100,-20), NSMakeSize(28,40), NSMakePoint(5,5)), NSMakeRect(-105,-55,28,40)), "negative screen coordinates");
    CHECK(!ValidSize(NSMakeSize(INFINITY, 1)) && !ValidSize(NSZeroSize), "invalid image size");
    CHECK(!ValidPoint(NSMakePoint(NAN, 0)), "invalid point");
    NSRect previousFrame = NSMakeRect(95,65,28,40), movedFrame = NSMakeRect(-105,-55,28,40);
    CHECK(!NeedsContentDisplay(NO, YES, previousFrame, movedFrame), "translation keeps drawn content");
    CHECK(NeedsContentDisplay(YES, YES, previousFrame, previousFrame), "changed glyph redraws");
    CHECK(NeedsContentDisplay(NO, NO, previousFrame, previousFrame), "first show redraws");
    CHECK(NeedsContentDisplay(NO, YES, previousFrame, NSMakeRect(95,65,56,80)), "changed size redraws");
    CHECK(BoundedData(28,40,112,4480), "bounded provider");
    CHECK(!BoundedData(28,0,112,4480), "empty raster");
    CHECK(!BoundedData(28,40,SIZE_MAX,4480), "stride overflow");
    CHECK(!BoundedData(28,40,112,DataLimit + 1), "large provider");
    NSData *a = [@"cursor pixels" dataUsingEncoding:NSUTF8StringEncoding];
    NSData *b = [@"cursor pixels" dataUsingEncoding:NSUTF8StringEncoding];
    NSData *changed = [@"other pixels" dataUsingEncoding:NSUTF8StringEncoding];
    CHECK(SameBytes(a, NSMakeSize(28,40), b, NSMakeSize(28,40)), "equal provider copies");
    CHECK(!SameBytes(a, NSMakeSize(28,40), changed, NSMakeSize(28,40)), "changed provider");
    CHECK(!SameBytes(a, NSMakeSize(28,40), b, NSMakeSize(56,80)), "changed point size");
    CHECK(!SameBytes(a, NSMakeSize(28,40), nil, NSMakeSize(28,40)), "missing provider");
    Display mixed[] = {{1,YES,NO,CGRectMake(0,0,1920,1080)},
        {2,NO,NO,CGRectMake(1920,0,1696,1200)}, {3,NO,YES,CGRectMake(0,0,1920,1080)}};
    NSUInteger candidates;
    const Display *chosen = SelectDisplay(mixed, 3, "extended", &candidates);
    CHECK(chosen && chosen->identifier == 2 && candidates == 1, "unique nonmirrored extension");
    chosen = SelectDisplay(mixed, 3, "main", &candidates);
    CHECK(chosen && chosen->identifier == 1 && candidates == 1, "main selection");
    CHECK(!SelectDisplay(NULL, 0, "extended", &candidates) && candidates == 0, "no extension refused");
    Display multiple[] = {{2,NO,NO,CGRectMake(0,0,100,100)}, {4,NO,NO,CGRectMake(-100,0,100,100)}};
    CHECK(!SelectDisplay(multiple, 2, "extended", &candidates) && candidates == 2, "ambiguous extension refused");
    Display invalidDisplay[] = {{0,YES,NO,CGRectMake(0,0,100,100)}};
    CHECK(!SelectDisplay(invalidDisplay, 1, "main", &candidates), "null display refused");
    CGPoint center;
    CHECK(DisplayCenter(CGRectMake(-1696,-120,1696,1200), &center) &&
        CGPointEqualToPoint(center, CGPointMake(-848,480)), "negative-origin display center");
    // Pixel dimensions are deliberately absent from selection and center calculations.
    CHECK(DisplayCenter(mixed[1].bounds, &center) && CGPointEqualToPoint(center, CGPointMake(2768,600)),
        "mixed-density logical center independent of 2x backing pixels");
    CHECK(!DisplayCenter(CGRectMake(0,0,NAN,1200), &center), "nonfinite display refused");
    CHECK(!DisplayCenter(CGRectMake(0,0,0,1200), &center), "empty display refused");
    double number;
    CHECK(!Number(@YES, &number) && !Number(@"1", &number), "numeric fields refuse booleans and strings");
    CHECK(!Number(@(NAN), &number) && !Number(@(INFINITY), &number), "numeric fields refuse nonfinite values");
    NSDictionary *properties = @{(__bridge NSString *)kCGImagePropertyPixelWidth:@1,
        (__bridge NSString *)kCGImagePropertyPixelHeight:@1, (__bridge NSString *)kCGImagePropertyDepth:@8};
    CHECK(PNGPropertiesValid(properties, 1, 1), "bounded PNG metadata");
    CHECK(!PNGPropertiesValid(properties, 2, 1), "PNG metadata must match header dimensions");
    NSMutableDictionary *oversized = [properties mutableCopy];
    oversized[(__bridge NSString *)kCGImagePropertyPixelWidth] = @4096;
    oversized[(__bridge NSString *)kCGImagePropertyPixelHeight] = @4096;
    CHECK(!PNGPropertiesValid(oversized, 4096, 4096), "decoded PNG byte bound checked before pixel allocation");
    oversized[(__bridge NSString *)kCGImagePropertyPixelWidth] = @1024;
    oversized[(__bridge NSString *)kCGImagePropertyPixelHeight] = @1024;
    oversized[(__bridge NSString *)kCGImagePropertyDepth] = @16;
    CHECK(!PNGPropertiesValid(oversized, 1024, 1024), "16-bit PNG expansion is bounded");
    oversized[(__bridge NSString *)kCGImagePropertyPixelWidth] = @YES;
    CHECK(!PNGPropertiesValid(oversized, 1, 4096), "PNG dimension must be an integer, not boolean");
    NSBitmapImageRep *rep = [[NSBitmapImageRep alloc] initWithBitmapDataPlanes:NULL pixelsWide:1 pixelsHigh:1
        bitsPerSample:8 samplesPerPixel:4 hasAlpha:YES isPlanar:NO colorSpaceName:NSCalibratedRGBColorSpace bytesPerRow:4 bitsPerPixel:32];
    if (!rep || !rep.bitmapData || !rep.CGImage) { fprintf(stderr, "Cannot create PNG test fixture\n"); return 1; }
    memset(rep.bitmapData, 0xff, 4);
    CursorGlyph *fixture = [CursorGlyph new];
    fixture.raster = CGImageRetain(rep.CGImage);
    fixture.data = [NSData dataWithBytes:rep.bitmapData length:4];
    fixture.size = NSMakeSize(2, 2);
    fixture.hotSpot = NSMakePoint(-1, 3);
    NSData *encoded = EncodeGlyph(fixture);
    if (!encoded) { fprintf(stderr, "Cannot encode PNG test fixture\n"); return 1; }
    CursorGlyph *decoded = nil;
    CHECK(encoded && DecodeGlyph(encoded, &decoded) && NSEqualSizes(decoded.size, fixture.size) &&
        NSEqualPoints(decoded.hotSpot, fixture.hotSpot), "PNG snapshot preserves logical size and hotspot");
    CHECK(!PNGPreflight([@"not a PNG" dataUsingEncoding:NSUTF8StringEncoding], 1, 1), "non-PNG payload refused");
    CHECK(SameSnapshot(nil, nil), "repeated unavailable snapshot unchanged");
    CursorGlyph *shifted = [CursorGlyph new];
    shifted.raster = CGImageRetain(fixture.raster); shifted.data = fixture.data;
    shifted.size = fixture.size; shifted.hotSpot = NSMakePoint(0, 3);
    CHECK(!SameSnapshot(fixture, shifted), "hotspot-only change must be published");
    NSMutableDictionary *bad = [[NSJSONSerialization JSONObjectWithData:encoded options:NSJSONReadingMutableContainers error:NULL] mutableCopy];
    bad[@"available"] = @1;
    CHECK(!DecodeGlyph([NSJSONSerialization dataWithJSONObject:bad options:0 error:NULL], &decoded), "availability must be boolean");
    bad[@"available"] = @YES; bad[@"pixelSize"] = @{@"width":@2, @"height":@1};
    CHECK(!DecodeGlyph([NSJSONSerialization dataWithJSONObject:bad options:0 error:NULL], &decoded), "PNG/header mismatch refused");
    bad[@"pixelSize"] = @{@"width":@1, @"height":@1}; bad[@"png"] = NSNull.null;
    CHECK(!DecodeGlyph([NSJSONSerialization dataWithJSONObject:bad options:0 error:NULL], &decoded), "available snapshot requires PNG data");
    NSData *unavailable = EncodeGlyph(nil);
    CHECK(DecodeGlyph(unavailable, &decoded) && !decoded, "actual unavailable snapshot clears image");
    uint32_t prefix = htonl((uint32_t)encoded.length);
    NSMutableData *frame = [NSMutableData dataWithBytes:&prefix length:sizeof(prefix)];
    [frame appendData:encoded];
    NSMutableData *buffer = [NSMutableData dataWithBytes:frame.bytes length:2];
    NSData *latest = nil;
    CHECK(ExtractLatest(buffer, &latest) && !latest && buffer.length == 2, "partial prefix does not wait");
    [buffer appendBytes:(const unsigned char *)frame.bytes + 2 length:3];
    CHECK(ExtractLatest(buffer, &latest) && !latest && buffer.length == 5, "partial payload does not replace snapshot");
    [buffer appendBytes:(const unsigned char *)frame.bytes + 5 length:frame.length - 5];
    CHECK(ExtractLatest(buffer, &latest) && [latest isEqualToData:encoded] && !buffer.length, "fragmented frame completes");
    [buffer appendData:frame];
    prefix = htonl((uint32_t)unavailable.length);
    [buffer appendBytes:&prefix length:sizeof(prefix)]; [buffer appendData:unavailable];
    CHECK(ExtractLatest(buffer, &latest) && [latest isEqualToData:unavailable] && !buffer.length, "only newest complete snapshot selected");
    prefix = htonl((uint32_t)WireLimit + 1);
    [buffer appendBytes:&prefix length:sizeof(prefix)];
    CHECK(!ExtractLatest(buffer, &latest), "oversized frame refused before allocation");
    [buffer setLength:0]; prefix = 0; [buffer appendBytes:&prefix length:sizeof(prefix)];
    CHECK(!ExtractLatest(buffer, &latest), "empty frame refused");
    int descriptors[2];
    int pipeResult = pipe(descriptors);
    CHECK(pipeResult == 0, "create idle worker test pipe");
    if (pipeResult == 0) {
        int flags = fcntl(descriptors[0], F_GETFL);
        BOOL nonblocking = flags != -1 && fcntl(descriptors[0], F_SETFL, flags | O_NONBLOCK) != -1;
        CHECK(nonblocking, "test pipe is nonblocking");
        if (nonblocking) {
            [buffer setLength:0];
            CHECK(ReadLatest(descriptors[0], buffer, &latest) && !latest && !buffer.length &&
                !NeedsContentDisplay(NO, YES, previousFrame, movedFrame), "EAGAIN leaves movement independent of worker progress");
            close(descriptors[1]); descriptors[1] = -1;
            CHECK(!ReadLatest(descriptors[0], buffer, &latest), "worker EOF is an explicit failure");
        }
        close(descriptors[0]); if (descriptors[1] != -1) close(descriptors[1]);
    }
#undef CHECK
    printf("{\"self_test\":\"%s\",\"checks\":%lu,\"failures\":%lu}\n", failures ? "failed" : "passed", (unsigned long)checks, (unsigned long)failures);
    return failures ? 1 : 0;
}

static void RequestStop(int signalNumber) { (void)signalNumber; StopRequested = 1; }

static void DrainOwnEvents(NSApplication *application) {
    // Only this application's queue; no event tap, global monitor or input interception.
    for (NSUInteger i = 0; i < 64; i++) {
        NSEvent *event = [application nextEventMatchingMask:NSEventMaskAny untilDate:NSDate.distantPast
            inMode:NSDefaultRunLoopMode dequeue:YES];
        if (!event) break;
        [application sendEvent:event];
    }
}

static int RunAid(double duration) {
    NSPanel *panel = nil;
    GlyphWorker *worker = nil;
    double start = Now(), deadline = duration ? start + duration : INFINITY;
    if (duration && !isfinite(deadline)) return 2;
    int result = 0;
    @try {
        NSApplication *application = NSApplication.sharedApplication;
        [application setActivationPolicy:NSApplicationActivationPolicyAccessory];
        panel = [[NSPanel alloc] initWithContentRect:NSMakeRect(0,0,1,1)
            styleMask:NSWindowStyleMaskNonactivatingPanel backing:NSBackingStoreBuffered defer:NO];
        panel.title = @"Reims Cursor Aid";
        panel.releasedWhenClosed = NO;
        panel.opaque = NO;
        panel.backgroundColor = NSColor.clearColor;
        panel.hasShadow = NO;
        panel.ignoresMouseEvents = YES;
        panel.acceptsMouseMovedEvents = NO;
        panel.hidesOnDeactivate = NO;
        panel.becomesKeyOnlyIfNeeded = YES;
        panel.level = CGWindowLevelForKey(kCGCursorWindowLevelKey);
        panel.collectionBehavior = NSWindowCollectionBehaviorCanJoinAllSpaces | NSWindowCollectionBehaviorFullScreenAuxiliary |
            NSWindowCollectionBehaviorStationary;
        NSImageView *view = [[NSImageView alloc] initWithFrame:NSMakeRect(0,0,1,1)];
        view.imageScaling = NSImageScaleNone;
        panel.contentView = view;
        CursorGlyph *cache = nil;
        BOOL shown = NO, dirtyGlyph = NO;
        NSRect previous = NSZeroRect;
        worker = StartGlyphWorker();
        if (!worker) { fprintf(stderr, "Cannot start cursor snapshot worker\n"); return 1; }
        double nextPosition = Now();
        while (!StopRequested && Now() < deadline) { @autoreleasepool {
            double now = Now();
            if (now >= nextPosition) {
                do { nextPosition += 1.0 / 60.0; } while (nextPosition <= now);
                DrainOwnEvents(application);
                NSData *packet;
                if (!ReadLatest(worker.input.fileDescriptor, worker.buffer, &packet)) {
                    fprintf(stderr, "Cursor snapshot worker closed or sent an invalid frame\n"); result = 1; break;
                }
                if (packet) {
                    CursorGlyph *fresh;
                    if (!DecodeGlyph(packet, &fresh)) { fprintf(stderr, "Invalid cursor snapshot\n"); result = 1; break; }
                    cache = fresh;
                    if (fresh) view.image = fresh.image;
                    dirtyGlyph = YES;
                }
                if (!CGCursorIsVisible() || !cache) {
                    if (shown) { [panel orderOut:nil]; shown = NO; }
                } else {
                    NSPoint mouse = NSEvent.mouseLocation;
                    if (ValidPoint(mouse)) {
                        NSRect frame = CursorFrame(mouse, cache.size, cache.hotSpot);
                        BOOL dirty = NeedsContentDisplay(dirtyGlyph, shown, previous, frame);
                        if (!NSEqualRects(previous, frame)) { [panel setFrame:frame display:NO]; previous = frame; }
                        if (!shown) { [panel orderFrontRegardless]; shown = YES; }
                        if (dirty) [panel displayIfNeeded];
                        dirtyGlyph = NO;
                    }
                }
                [application updateWindows];
            }
            double remaining = fmin(nextPosition, deadline) - Now();
            if (remaining > 0) [NSRunLoop.currentRunLoop runUntilDate:[NSDate dateWithTimeIntervalSinceNow:remaining]];
        }}
    } @catch (NSException *exception) {
        fprintf(stderr, "%s: %s\n", exception.name.UTF8String, exception.reason.UTF8String);
        result = 1;
    } @finally {
        [worker.input closeFile];
        if (worker.task.running) [worker.task terminate];
        [panel orderOut:nil];
        [panel close];
    }
    return result;
}

int main(int argc, const char *argv[]) { @autoreleasepool {
    Options options;
    if (!ParseOptions(argc, argv, &options)) {
        fprintf(stderr, "Usage: ReimsCursorAid [--duration SECONDS | --self-test | --move-to-display main|extended]\n");
        return 2;
    }
    if (options.selfTest) return SelfTest();
    if (options.moveTarget) return MoveToDisplay(options.moveTarget);
    if (options.glyphWorker) return RunGlyphWorker();
    struct sigaction action = {0};
    action.sa_handler = RequestStop;
    sigemptyset(&action.sa_mask);
    if (sigaction(SIGTERM, &action, NULL) || sigaction(SIGINT, &action, NULL)) { perror("sigaction"); return 1; }
    return RunAid(options.duration);
}}
