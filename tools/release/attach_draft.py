"""Append a verified launcher and checksum to an already seeded GitHub draft."""

from dataclasses import dataclass
import hashlib
import http.client
import json
import os
from pathlib import Path
import re
import stat
import subprocess
import sys
import tempfile

REPOSITORY = "Flinterino/wz-sync"
API_PREFIX = f"repos/{REPOSITORY}"
MANIFESTS = frozenset(
    f"manifest-{pack}.json{suffix}" for pack in ("full", "lite") for suffix in ("", ".sha256")
)
EXE = "WzSync.App.exe"
CHECKSUM = EXE + ".sha256"
OUTPUTS = (EXE, CHECKSUM)
TAG = re.compile(r"[A-Za-z0-9][A-Za-z0-9._-]{0,99}")
COMMIT = re.compile(r"[0-9a-f]{40}")
SHA256 = re.compile(r"[0-9a-f]{64}")
OWNED = re.compile(r"[0-9a-f]{64}\.(?:jar|json|toml|properties)")


class ReleaseError(Exception):
    pass


@dataclass(frozen=True)
class Asset:
    id: int
    name: str
    size: int


@dataclass(frozen=True)
class Draft:
    id: int
    tag: str
    assets: tuple[Asset, ...]


def asset_inventory(draft):
    assets = {}
    ids = set()
    for asset in draft.assets:
        name = asset.name
        if name not in MANIFESTS and name not in OUTPUTS and not OWNED.fullmatch(name):
            raise ReleaseError("The draft contains an unsupported asset name.")
        if name in assets or asset.id in ids or type(asset.id) is not int or asset.id <= 0:
            raise ReleaseError("The draft contains duplicate or invalid asset identifiers.")
        limit = 256 if name.endswith(".sha256") else 8 * 1024**2 if name in MANIFESTS else 2 * 1024**3 - 1
        if type(asset.size) is not int or not 0 <= asset.size <= limit:
            raise ReleaseError("A draft asset exceeds its size limit.")
        assets[name] = asset
        ids.add(asset.id)
    if not MANIFESTS.issubset(assets):
        raise ReleaseError("The draft must already contain both manifest/checksum pairs.")
    return assets


def regular_file(path):
    attributes = path.lstat()
    return (stat.S_ISREG(attributes.st_mode)
            and not getattr(attributes, "st_file_attributes", 0) & stat.FILE_ATTRIBUTE_REPARSE_POINT)


def digest_file(path):
    with path.open("rb") as file:
        return hashlib.file_digest(file, "sha256").hexdigest()


def verify_launcher(launcher, expected):
    if not SHA256.fullmatch(expected):
        raise ReleaseError("The build did not supply a valid executable SHA-256.")
    if launcher.is_symlink() or {path.name for path in launcher.iterdir()} != set(OUTPUTS):
        raise ReleaseError("The build artifact must contain just the launcher and checksum.")
    exe, checksum = (launcher / name for name in OUTPUTS)
    if not regular_file(exe) or not regular_file(checksum) or exe.stat().st_size == 0:
        raise ReleaseError("The build artifact contains an invalid file.")
    expected_sidecar = f"{expected}  {EXE}\n".encode("ascii")
    if (checksum.stat().st_size != len(expected_sidecar) or checksum.read_bytes() != expected_sidecar
            or digest_file(exe) != expected):
        raise ReleaseError("The build artifact does not match its verified SHA-256.")


def check_draft_output(api, asset, local, temporary):
    if asset.size != local.stat().st_size:
        raise ReleaseError("A draft launcher asset conflicts with the verified build.")
    api.download(asset, temporary)
    if digest_file(temporary) != digest_file(local):
        raise ReleaseError("A draft launcher asset conflicts with the verified build.")


def attach(api, launcher, tag, commit, expected_hash, validate_bundle):
    if (not TAG.fullmatch(tag) or tag.endswith((".", ".lock")) or ".." in tag
            or not COMMIT.fullmatch(commit)):
        raise ReleaseError("The release must use a valid tag and known source commit.")
    verify_launcher(launcher, expected_hash)
    if api.tag_commit(tag) != commit:
        raise ReleaseError("The release tag does not point to the built commit.")

    draft = api.snapshot(tag)
    assets = asset_inventory(draft)
    with tempfile.TemporaryDirectory(prefix="wz-sync-draft-") as directory:
        stage = Path(directory)
        bundle = stage / "bundle"
        bundle.mkdir()
        for name, asset in assets.items():
            if name not in OUTPUTS:
                api.download(asset, bundle / name)
        validate_bundle(bundle, tag)

        pending = []
        for name in OUTPUTS:
            local = launcher / name
            if name not in assets:
                pending.append(local)
            else:
                check_draft_output(api, assets[name], local, stage / name)

        expected_assets = assets.copy()
        for path in pending:
            require_snapshot(api, draft, commit, expected_assets)
            expected_assets[path.name] = api.upload(draft, path)
            check_draft_output(api, expected_assets[path.name], path, stage / path.name)
        require_snapshot(api, draft, commit, expected_assets)


def require_snapshot(api, draft, commit, expected_assets):
    current = api.snapshot(draft.tag)
    if (current.id != draft.id or asset_inventory(current) != expected_assets
            or api.tag_commit(draft.tag) != commit):
        raise ReleaseError("The draft or tag changed during verification.")


class GitHub:
    def __init__(self):
        self.env = dict(os.environ, GH_HOST="github.com", GH_PROMPT_DISABLED="1",
                        GH_NO_UPDATE_NOTIFIER="1", GH_NO_EXTENSION_UPDATE_NOTIFIER="1")

    def api(self, endpoint, paginate=False):
        args = ["gh", "api", "--hostname", "github.com", endpoint]
        if paginate:
            args.extend(("--paginate", "--slurp"))
        result = subprocess.run(args, env=self.env, check=True, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL)
        data = json.loads(result.stdout)
        return [row for page in data for row in page] if paginate else data

    def snapshot(self, tag):
        matches = [row for row in self.api(f"{API_PREFIX}/releases?per_page=100", paginate=True)
                   if row["tag_name"] == tag]
        if len(matches) != 1 or matches[0]["draft"] is not True:
            raise ReleaseError("Stage the approved bundle in a matching GitHub draft before running this workflow.")
        release_id = matches[0]["id"]
        if type(release_id) is not int or release_id <= 0:
            raise ReleaseError("Invalid GitHub draft identifier.")
        records = self.api(f"{API_PREFIX}/releases/{release_id}/assets?per_page=100", paginate=True)
        if any(row["state"] != "uploaded" for row in records):
            raise ReleaseError("A draft asset has not finished uploading.")
        return Draft(release_id, tag, tuple(Asset(row["id"], row["name"], row["size"]) for row in records))

    def tag_commit(self, tag):
        target = self.api(f"{API_PREFIX}/git/ref/tags/{tag}")["object"]
        for _ in range(5):
            oid = target["sha"]
            if not COMMIT.fullmatch(oid):
                raise ReleaseError("Unexpected GitHub tag object.")
            if target["type"] == "commit":
                return oid
            if target["type"] != "tag":
                break
            target = self.api(f"{API_PREFIX}/git/tags/{oid}")["object"]
        raise ReleaseError("The release tag must resolve to a commit.")

    def download(self, asset, path):
        args = ["gh", "api", "--hostname", "github.com", f"{API_PREFIX}/releases/assets/{asset.id}",
                "-H", "Accept: application/octet-stream"]
        with path.open("xb") as output, subprocess.Popen(
                args, env=self.env, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL) as process:
            size = 0
            for chunk in iter(lambda: process.stdout.read(65536), b""):
                size += len(chunk)
                if size > asset.size:
                    process.kill()
                    raise ReleaseError("A GitHub asset exceeded its declared byte size.")
                output.write(chunk)
            if process.wait() != 0 or size != asset.size:
                raise ReleaseError("A GitHub asset could not be downloaded at its declared size.")

    def upload(self, draft, path):
        if type(draft.id) is not int or draft.id <= 0 or path.name not in OUTPUTS:
            raise ReleaseError("Invalid draft upload request.")
        release = self.api(f"{API_PREFIX}/releases/{draft.id}")
        if release["id"] != draft.id or release["tag_name"] != draft.tag or release["draft"] is not True:
            raise ReleaseError("The verified draft changed before attachment.")

        size = path.stat().st_size
        token = self.env["GH_TOKEN"]
        connection = http.client.HTTPSConnection("uploads.github.com", timeout=120)
        try:
            with path.open("rb") as payload:
                connection.request("POST", f"/{API_PREFIX}/releases/{draft.id}/assets?name={path.name}",
                                   body=payload, headers={
                                       "Authorization": f"Bearer {token}",
                                       "Accept": "application/vnd.github+json",
                                       "Content-Type": "application/octet-stream",
                                       "Content-Length": str(size),
                                       "User-Agent": "wz-sync-release-workflow",
                                   })
                with connection.getresponse() as response:
                    if response.status != 201:
                        raise ReleaseError(f"GitHub refused the append-only asset upload (HTTP {response.status}).")
                    asset = json.loads(response.read(8192))
        finally:
            connection.close()
        if (asset["name"] != path.name or asset["size"] != size or asset["state"] != "uploaded"
                or type(asset["id"]) is not int or asset["id"] <= 0):
            raise ReleaseError("GitHub did not return the expected uploaded asset.")
        return Asset(asset["id"], path.name, size)


def main():
    root = Path(__file__).resolve().parents[2]
    validator = root / "tools/WzSync.Release/bin/Release/net10.0/WzSync.Release.dll"

    def validate_bundle(bundle, tag):
        local_env = {key: value for key, value in os.environ.items()
                     if key not in {"GH_TOKEN", "GITHUB_TOKEN", "GH_ENTERPRISE_TOKEN", "GITHUB_ENTERPRISE_TOKEN"}}
        subprocess.run(["dotnet", str(validator), str(bundle), tag], env=local_env, check=True)

    try:
        if os.environ.get("GITHUB_REPOSITORY") != REPOSITORY or os.environ.get("GITHUB_REF_TYPE") != "tag":
            raise ReleaseError("Run draft attachment only from a tag in Flinterino/wz-sync.")
        commit = subprocess.check_output(["git", "-C", str(root), "rev-parse", "HEAD"], text=True).strip()
        attach(GitHub(), Path(os.environ["LAUNCHER_DIRECTORY"]), os.environ["GITHUB_REF_NAME"],
               commit, os.environ["EXPECTED_EXE_SHA256"], validate_bundle)
        print("Verified the approved draft bundle and its launcher assets. The release remains a draft.")
        return 0
    except ReleaseError as error:
        print(error, file=sys.stderr)
    except (OSError, KeyError, ValueError, http.client.HTTPException, subprocess.SubprocessError):
        print("Could not verify or attach release assets. The workflow never replaces an existing asset.",
              file=sys.stderr)
    return 1


if __name__ == "__main__":
    sys.exit(main())
