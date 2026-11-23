{
  inputs = {
    nixpkgs.url = "github:NixOS/nixpkgs/nixpkgs-unstable";
    treefmt-nix.url = "github:numtide/treefmt-nix";
  };

  outputs =
    {
      nixpkgs,
      treefmt-nix,
      ...
    }:
    let
      system = "x86_64-linux";
      pkgs = import nixpkgs { inherit system; };
      treefmtEval = treefmt-nix.lib.evalModule pkgs ./treefmt.nix;
      inherit (pkgs) mkShell;
    in
    {
      formatter.${system} = treefmtEval.config.build.wrapper;
      devShell."${system}" = mkShell {
        nativeBuildInputs = with pkgs; [
          # the server: .NET 10 SDK, no third-party packages (`dotnet.sh` runs it)
          dotnet-sdk_10
          # the client: the same major Node the image builds with (node:22-alpine)
          nodejs_22
          # tools/gun_report.py (ffmpeg reads the recording, numpy does the FFT)
          (python3.withPackages (ps: [ ps.numpy ]))
          ffmpeg
          # dev.sh starts the server in a tmux session
          tmux
          # editor: Zed's C# extension downloads a framework-dependent Roslyn
          # apphost, which cannot find a .NET runtime on NixOS; this is the
          # wrapped build the project's .zed/settings.json points at
          roslyn-ls
        ];
      };
    };
}
