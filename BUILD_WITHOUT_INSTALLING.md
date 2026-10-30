# Get Island.exe without installing anything (uses free GitHub servers)

1. Create a free account at https://github.com and click **New repository** (name it `IslandEye`, Public).
2. On the new repo page click **uploading an existing file** and drag in **everything inside the
   unzipped IslandEye folder** (including the hidden `.github` folder — easiest is to
   drag the whole folder contents in one go). Click **Commit changes**.
3. Open the **Actions** tab. If asked, click "I understand my workflows, enable them".
4. Click **Build** (left side) -> **Run workflow** -> green button.
5. Wait ~3 minutes until it shows a green tick. Click the run, scroll to **Artifacts**,
   download **Island-win-x64** (a zip containing `Island.exe`).
6. Unzip and double-click `Island.exe`.

Tip: for a permanent download link, create a Release with a tag like `v1.0.1`;
the workflow attaches Island.exe to it automatically.
