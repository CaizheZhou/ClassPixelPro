{
  dotnetCorePackages,
  buildDotnetModule,
  libx11,
  libice,
  libsm,
  libxfixes,
  fontconfig,
  git,
  stdenv,
  autoPatchelfHook,
  makeDesktopItem,
}:
let
  desktopItem = makeDesktopItem {
    type = "Application";
    name = "cn.classpixelpro.app";
    desktopName = "ClassPixelPro";
    icon = "cn.classpixelpro.app";
    exec = "classpixelpro";
    terminal = false;
    startupNotify = true;
    comment = "功能强大、可定制、跨平台的大屏课表显示工具。";
    categories = [
      "Education"
      "Office"
    ];
  };
in
buildDotnetModule {
  pname = "classpixelpro";
  version = "2.1.1.0";
  projectFile = "./ClassPixelPro.Desktop/ClassPixelPro.Desktop.csproj";
  dotnet-sdk =
    with dotnetCorePackages;
    (combinePackages [
      sdk_9_0
      sdk_8_0
      sdk_6_0
    ]);
  dotnet-runtime = dotnetCorePackages.runtime_8_0;
  src = ../../.;
  # nix build .#classpixelpro.passthru.fetch-deps && ./result ./tools/nix/deps.json
  # 生成完后可能需要手动修改
  nugetDeps = ./deps.json;
  doCheck = true;
  dotnetBuildFlags = [
    "--property:NIX=true"
  ];
  runtimeDeps = [
    libx11
    libice
    libsm
    libxfixes
    fontconfig
  ];
  executables = [ "ClassPixelPro.Desktop" ];
  nativeBuildInputs = [
    git
    stdenv.cc.cc.lib
    autoPatchelfHook
  ];
  postInstall = ''
    mkdir -p $out/share/applications
    cp ${desktopItem}/share/applications/cn.classpixelpro.app.desktop $out/share/applications/cn.classpixelpro.app.desktop 
    mkdir -p $out/share/icons/hicolor/scalable/apps/
    cp ClassPixelPro.Desktop/Assets/AppLogo.svg $out/share/icons/hicolor/scalable/apps/cn.classpixelpro.app.svg
    printf deb > $out/lib/classpixelpro/PackageType
  '';
  postFixup = ''
    mv $out/bin/ClassPixelPro.Desktop $out/bin/classpixelpro
  '';
  packNupkg = false;
}
