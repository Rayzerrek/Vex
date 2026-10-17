{
  description = "Vex native Windows cross-build environment for Linux/WSL";

  inputs.nixpkgs.url = "github:NixOS/nixpkgs/nixos-unstable";

  outputs = { nixpkgs, ... }:
    let
      systems = [ "x86_64-linux" "aarch64-linux" ];
      forAllSystems = nixpkgs.lib.genAttrs systems;
      toolchain = system:
        let
          pkgs = import nixpkgs { inherit system; };
          version = pkgs.lib.removeSuffix "\n" (builtins.readFile ./native/.zig-version);
          platforms = {
            x86_64-linux = {
              arch = "x86_64";
              sha256 = "02aa270f183da276e5b5920b1dac44a63f1a49e55050ebde3aecc9eb82f93239";
            };
            aarch64-linux = {
              arch = "aarch64";
              sha256 = "958ed7d1e00d0ea76590d27666efbf7a932281b3d7ba0c6b01b0ff26498f667f";
            };
          };
          platform = platforms.${system};
          zig = pkgs.stdenvNoCC.mkDerivation {
            pname = "zig";
            inherit version;
            src = pkgs.fetchurl {
              url = "https://ziglang.org/download/${version}/zig-${platform.arch}-linux-${version}.tar.xz";
              inherit (platform) sha256;
            };
            dontBuild = true;
            dontStrip = true;
            installPhase = ''
              runHook preInstall
              mkdir -p "$out/lib/zig" "$out/bin"
              cp -r . "$out/lib/zig/"
              ln -s "$out/lib/zig/zig" "$out/bin/zig"
              runHook postInstall
            '';
          };
        in { inherit pkgs zig; };
    in {
      packages = forAllSystems (system: { default = (toolchain system).zig; });
      devShells = forAllSystems (system:
        let tools = toolchain system;
        in {
          default = tools.pkgs.mkShellNoCC {
            packages = [ tools.zig tools.pkgs.python3 tools.pkgs.git tools.pkgs.gh tools.pkgs.actionlint ];
          };
        });
    };
}
