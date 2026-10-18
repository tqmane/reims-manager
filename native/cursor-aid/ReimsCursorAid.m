#import <AppKit/AppKit.h>
#import <CoreGraphics/CoreGraphics.h>
#include <errno.h>
#include <math.h>
#include <signal.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

static const NSUInteger DataLimit = 4 * 1024 * 1024;
static volatile sig_atomic_t StopRequested;

typedef struct { BOOL selfTest; double duration; const char *moveTarget; } Options;
typedef struct { CGDirectDisplayID identifier; BOOL main, mirrored; CGRect bounds; } Display;

static BOOL ParseOptions(int argc, const char *const argv[], Options *out) {
    *out = (Options){NO, 0, NULL};
    if (argc == 1) return YES;
    if (argc == 2 && !strcmp(argv[1], "--self-test")) {
        out->selfTest = YES;
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
        panel.collectionBehavior = NSWindowCollectionBehaviorCanJoinAllSpaces | NSWindowCollectionBehaviorFullScreenAuxiliary;
        NSImageView *view = [[NSImageView alloc] initWithFrame:NSMakeRect(0,0,1,1)];
        view.imageScaling = NSImageScaleNone;
        panel.contentView = view;
        CursorGlyph *cache = nil;
        BOOL shown = NO, dirtyGlyph = NO;
        NSRect previous = NSZeroRect;
        double nextPosition = Now(), nextGlyph = nextPosition;
        while (!StopRequested && Now() < deadline) { @autoreleasepool {
            double now = Now();
            if (now >= nextPosition) {
                do { nextPosition += 1.0 / 60.0; } while (nextPosition <= now);
                DrainOwnEvents(application);
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
            now = Now();
            if (now >= nextGlyph) {
                do { nextGlyph += 0.1; } while (nextGlyph <= now);
                CursorGlyph *fresh = ReadGlyph();
                if (!SameGlyph(cache, fresh)) { if (fresh) view.image = fresh.image; dirtyGlyph = YES; }
                cache = fresh;
            }
            double remaining = fmin(nextPosition, fmin(nextGlyph, deadline)) - Now();
            if (remaining > 0) [NSRunLoop.currentRunLoop runUntilDate:[NSDate dateWithTimeIntervalSinceNow:remaining]];
        }}
    } @catch (NSException *exception) {
        fprintf(stderr, "%s: %s\n", exception.name.UTF8String, exception.reason.UTF8String);
        result = 1;
    } @finally {
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
    struct sigaction action = {0};
    action.sa_handler = RequestStop;
    sigemptyset(&action.sa_mask);
    if (sigaction(SIGTERM, &action, NULL) || sigaction(SIGINT, &action, NULL)) { perror("sigaction"); return 1; }
    return RunAid(options.duration);
}}
