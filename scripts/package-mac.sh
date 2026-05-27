#!/bin/bash
set -e

# ------------------------------------------------------------
# App settings
# ------------------------------------------------------------

APP_NAME="logicom"
EXE_NAME="serial.Desktop"
BUNDLE_ID="com.jacob.logicom"

PROJECT="src/serial.Desktop/serial.Desktop.csproj"

ICON_PNG="assets/AppIcon.png"
ICONSET_DIR="AppIcon.iconset"
ICON_ICNS="AppIcon.icns"

# ------------------------------------------------------------
# Detect Mac architecture
# ------------------------------------------------------------

ARCH=$(uname -m)

if [ "$ARCH" = "arm64" ]; then
    RID="osx-arm64"
else
    RID="osx-x64"
fi

PUBLISH_DIR="publish/$RID"
APP_DIR="dist/$APP_NAME.app"

echo "Packaging $APP_NAME"
echo "Architecture: $ARCH"
echo "Runtime ID: $RID"
echo "Project: $PROJECT"
echo ""

# ------------------------------------------------------------
# Clean old build output
# ------------------------------------------------------------

rm -rf "$PUBLISH_DIR"
rm -rf "$APP_DIR"

# ------------------------------------------------------------
# Generate .icns icon from assets/AppIcon.png
# ------------------------------------------------------------

if [ -f "$ICON_PNG" ]; then
    echo "Generating app icon..."

    rm -rf "$ICONSET_DIR"
    mkdir "$ICONSET_DIR"

    sips -z 16 16       "$ICON_PNG" --out "$ICONSET_DIR/icon_16x16.png" >/dev/null
    sips -z 32 32       "$ICON_PNG" --out "$ICONSET_DIR/icon_16x16@2x.png" >/dev/null
    sips -z 32 32       "$ICON_PNG" --out "$ICONSET_DIR/icon_32x32.png" >/dev/null
    sips -z 64 64       "$ICON_PNG" --out "$ICONSET_DIR/icon_32x32@2x.png" >/dev/null
    sips -z 128 128     "$ICON_PNG" --out "$ICONSET_DIR/icon_128x128.png" >/dev/null
    sips -z 256 256     "$ICON_PNG" --out "$ICONSET_DIR/icon_128x128@2x.png" >/dev/null
    sips -z 256 256     "$ICON_PNG" --out "$ICONSET_DIR/icon_256x256.png" >/dev/null
    sips -z 512 512     "$ICON_PNG" --out "$ICONSET_DIR/icon_256x256@2x.png" >/dev/null
    sips -z 512 512     "$ICON_PNG" --out "$ICONSET_DIR/icon_512x512.png" >/dev/null
    sips -z 1024 1024   "$ICON_PNG" --out "$ICONSET_DIR/icon_512x512@2x.png" >/dev/null

    iconutil -c icns "$ICONSET_DIR" -o "$ICON_ICNS"
else
    echo "No icon found at $ICON_PNG. Continuing without custom icon."
fi

# ------------------------------------------------------------
# Publish .NET Avalonia app
# ------------------------------------------------------------

echo "Publishing .NET app..."

dotnet publish "$PROJECT" \
    -c Release \
    -r "$RID" \
    --self-contained true \
    -p:UseAppHost=true \
    -o "$PUBLISH_DIR"

# ------------------------------------------------------------
# Verify executable exists
# ------------------------------------------------------------

if [ ! -f "$PUBLISH_DIR/$EXE_NAME" ]; then
    echo ""
    echo "ERROR: Could not find executable:"
    echo "$PUBLISH_DIR/$EXE_NAME"
    echo ""
    echo "Files in publish directory:"
    ls -la "$PUBLISH_DIR"
    echo ""
    echo "Fix EXE_NAME in this script to match the extensionless executable above."
    exit 1
fi

# ------------------------------------------------------------
# Create macOS .app bundle structure
# ------------------------------------------------------------

echo "Creating .app bundle..."

mkdir -p "$APP_DIR/Contents/MacOS"
mkdir -p "$APP_DIR/Contents/Resources"

cp -R "$PUBLISH_DIR/"* "$APP_DIR/Contents/MacOS/"

chmod +x "$APP_DIR/Contents/MacOS/$EXE_NAME"

if [ -f "$ICON_ICNS" ]; then
    cp "$ICON_ICNS" "$APP_DIR/Contents/Resources/AppIcon.icns"
fi

# ------------------------------------------------------------
# Create Info.plist
# ------------------------------------------------------------

cat > "$APP_DIR/Contents/Info.plist" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN"
 "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleName</key>
    <string>$APP_NAME</string>

    <key>CFBundleDisplayName</key>
    <string>$APP_NAME</string>

    <key>CFBundleIdentifier</key>
    <string>$BUNDLE_ID</string>

    <key>CFBundleVersion</key>
    <string>1.0.0</string>

    <key>CFBundleShortVersionString</key>
    <string>1.0</string>

    <key>CFBundleExecutable</key>
    <string>$EXE_NAME</string>

    <key>CFBundlePackageType</key>
    <string>APPL</string>

    <key>CFBundleInfoDictionaryVersion</key>
    <string>6.0</string>

    <key>NSHighResolutionCapable</key>
    <true/>

    <key>CFBundleIconFile</key>
    <string>AppIcon</string>
</dict>
</plist>
EOF

# ------------------------------------------------------------
# Optional local ad-hoc signing
# ------------------------------------------------------------

echo "Ad-hoc signing app..."

codesign --force --deep --sign - "$APP_DIR"

# ------------------------------------------------------------
# Done
# ------------------------------------------------------------

echo ""
echo "Created app:"
echo "$APP_DIR"
echo ""
echo "Executable check:"
file "$APP_DIR/Contents/MacOS/$EXE_NAME"
echo ""
echo "Run it with:"
echo "open \"$APP_DIR\""
echo ""
echo "Install it to Applications with:"
echo "rm -rf \"/Applications/$APP_NAME.app\""
echo "cp -R \"$APP_DIR\" /Applications/"
echo "touch \"/Applications/$APP_NAME.app\""
echo "killall Finder"
