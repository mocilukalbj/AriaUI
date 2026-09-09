import subprocess
import os
import sys

base_dir = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
node_candidates = [
    os.environ.get("NODE_EXE", ""),
    r"C:\Users\LBJ\Desktop\app\node.exe",
    r"C:\Users\LBJ\AppData\Local\Zed\node\node-v24.11.0-win-x64\node.exe",
    r"C:\Users\LBJ\AppData\Local\OpenAI\Codex\runtimes\cua_node\b474a88d5d105afa\bin\node.exe",
    r"C:\Users\LBJ\AppData\Local\Programs\Antigravity IDE\Antigravity IDE.exe",
    "node"
]
node_exe = next((p for p in node_candidates if p and (os.path.exists(p) or p == "node")), "node")
env = dict(os.environ, ELECTRON_RUN_AS_NODE="1")

tests = [
    os.path.join(base_dir, "tests", "extension-request-observer.mjs"),
    os.path.join(base_dir, "tests", "extension-context-menu.mjs"),
    os.path.join(base_dir, "tests", "extension-settings.mjs"),
    os.path.join(base_dir, "tests", "extension-handoff.mjs"),
]

all_passed = True
for test_file in tests:
    print(f"Running {os.path.basename(test_file)}...")
    res = subprocess.run([node_exe, test_file], capture_output=True, text=True, env=env)
    if res.stdout:
        print(res.stdout.strip())
    if res.stderr:
        print(res.stderr.strip())
    if res.returncode != 0:
        print(f"FAILED: {os.path.basename(test_file)} (code {res.returncode})")
        all_passed = False
    else:
        print(f"PASSED: {os.path.basename(test_file)}\n")

if not all_passed:
    sys.exit(1)

should_pack = "--pack" in sys.argv or "--update-unpacked" in sys.argv
if should_pack:
    print("Running packaging...")
    pack_script = os.path.join(base_dir, "packaging", "pack_extension.mjs")
    pack_args = [node_exe, pack_script]
    if "--update-unpacked" in sys.argv:
        pack_args.append("--update-unpacked")
    res = subprocess.run(pack_args, capture_output=True, text=True, env=env)
    if res.stdout:
        print(res.stdout.strip())
    if res.stderr:
        print(res.stderr.strip())
    if res.returncode != 0:
        print(f"Packaging FAILED (code {res.returncode})")
        sys.exit(1)
    else:
        print("Packaging SUCCEEDED\n")
    print("ALL TESTS AND PACKAGING COMPLETED SUCCESSFULLY!")
else:
    print("ALL EXTENSION TESTS COMPLETED SUCCESSFULLY!")
