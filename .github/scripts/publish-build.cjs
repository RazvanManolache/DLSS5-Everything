// Publish one replaceable development download without consuming Actions storage.
const fs = require('node:fs');
const path = require('node:path');
const os = require('node:os');
const crypto = require('node:crypto');

module.exports = async ({github, context, core, glob, exec}) => {
  const repo = context.repo;
  const branch = context.payload.repository.default_branch;
  if (!['push', 'workflow_dispatch'].includes(context.eventName) || context.ref !== `refs/heads/${branch}`) {
    core.info('Persistent CI downloads are published only from the default branch. PR logs remain in Actions.');
    return;
  }
  const current = await github.rest.git.getRef({...repo, ref: `heads/${branch}`});
  if (current.data.object.sha !== context.sha) {
    core.info('A newer default-branch commit exists; preserving its downloads.');
    return;
  }
  const name = process.env.BUILD_NAME;
  if (!/^[A-Za-z0-9_.-]+$/.test(name)) throw new Error(`Unsafe build name: ${name}`);
  const finder = await glob.create(process.env.BUILD_PATHS, {
    followSymbolicLinks: false,
    omitHiddenFiles: process.env.BUILD_HIDDEN !== 'true'
  });
  const files = (await finder.glob()).filter(p => fs.lstatSync(p).isFile());
  if (!files.length) {
    if (process.env.BUILD_MISSING === 'error') throw new Error('No build files matched the download paths.');
    core.info('No optional build evidence to publish.');
    return;
  }
  const work = fs.mkdtempSync(path.join(os.tmpdir(), 'ci-download-'));
  try {
    let root = path.resolve(finder.getSearchPaths()[0]);
    if (fs.existsSync(root) && fs.statSync(root).isFile()) root = path.dirname(root);
    while (files.some(p => {const r = path.relative(root, p); return r.startsWith('..' + path.sep) || r === '..' || path.isAbsolute(r);})) {
      const parent = path.dirname(root);
      if (parent === root) throw new Error('Build paths have no safe common root.');
      root = parent;
    }
    const zip = path.join(work, `${name}.zip`);
    const manifest = path.join(work, 'files.json');
    fs.writeFileSync(manifest, JSON.stringify({root, files, zip, provenance: {
      repository: `${repo.owner}/${repo.repo}`, commit: context.sha,
      run: `https://github.com/${repo.owner}/${repo.repo}/actions/runs/${context.runId}`,
      name
    }}));
    await exec.exec(process.platform === 'win32' ? 'python' : 'python3', ['-c',
      'import json,sys,zipfile,os; d=json.load(open(sys.argv[1],encoding="utf-8")); z=zipfile.ZipFile(d["zip"],"w",zipfile.ZIP_DEFLATED,compresslevel=6); [z.write(p,os.path.relpath(p,d["root"])) for p in d["files"]]; z.writestr("CI-PROVENANCE.json",json.dumps(d["provenance"],indent=2)); z.close()', manifest]);
    const bytes = fs.statSync(zip).size;
    if (bytes > 1900 * 1024 * 1024) throw new Error('CI download exceeds the 1900 MiB safety limit; split the package.');
    let release;
    try { release = (await github.rest.repos.getReleaseByTag({...repo, tag: 'ci-latest'})).data; }
    catch (e) {
      if (e.status !== 404) throw e;
      try {
        release = (await github.rest.repos.createRelease({...repo, tag_name: 'ci-latest', target_commitish: context.sha,
          name: 'Latest CI builds (development)', prerelease: true, make_latest: 'false',
          body: 'Automatically replaced development downloads from the default branch. Each ZIP contains CI-PROVENANCE.json identifying its exact source commit and run. Platforms may finish at different times. Stable versioned releases are separate.'})).data;
      } catch (race) {
        if (race.status !== 422) throw race;
        release = (await github.rest.repos.getReleaseByTag({...repo, tag: 'ci-latest'})).data;
      }
    }
    const assets = await github.paginate(github.rest.repos.listReleaseAssets, {...repo, release_id: release.id, per_page: 100});
    const finalName = `${name}.zip`;
    if (assets.filter(a => a.name !== finalName).reduce((n,a) => n+a.size,0) + bytes > 4 * 1024 ** 3)
      throw new Error('The rolling CI release would exceed its 4 GiB budget.');
    const digest = crypto.createHash('sha256').update(fs.readFileSync(zip)).digest('hex');
    // Keep the last usable download until the replacement is safely uploaded.
    const uploaded = (await github.rest.repos.uploadReleaseAsset({...repo, release_id: release.id,
      name: `${name}.pending-${context.runId}-${process.env.GITHUB_RUN_ATTEMPT || '1'}.zip`,
      data: fs.readFileSync(zip), headers: {'content-type': 'application/zip', 'content-length': bytes}})).data;
    if (uploaded.size !== bytes || (uploaded.digest && uploaded.digest !== `sha256:${digest}`)) {
      await github.rest.repos.deleteReleaseAsset({...repo, asset_id: uploaded.id});
      throw new Error('Uploaded build failed size/digest verification; previous download preserved.');
    }
    for (const old of assets.filter(a => a.name === finalName || a.name.startsWith(`${name}.pending-`)))
      await github.rest.repos.deleteReleaseAsset({...repo, asset_id: old.id});
    const renamed = (await github.rest.repos.updateReleaseAsset({...repo, asset_id: uploaded.id, name: finalName,
      label: `${context.sha.slice(0,12)} | SHA256 ${digest}`})).data;
    core.setOutput('artifact-url', renamed.browser_download_url);
    await core.summary.addLink(`Download ${name}`, renamed.browser_download_url).addRaw(`\n\nSource: ${context.sha}\n\nSHA256: ${digest}\n`).write();
  } finally {
    // work is a freshly-created direct child of the OS temp directory.
    fs.rmSync(work, {recursive: true, force: true});
  }
};
