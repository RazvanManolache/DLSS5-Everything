module.exports = async ({github, context, core}) => {
  const repo = context.repo;
  const remove = async fn => { try { await fn(); } catch (e) { if (e.status !== 404) throw e; } };
  const metadata = (await github.rest.repos.get(repo)).data;
  if (metadata.size > 1024 * 1024) throw new Error('Git repository exceeds the 1 GiB review threshold. Move generated binaries to Releases.');
  const ref = context.payload.pull_request?.head.sha || context.sha;
  const tree = (await github.rest.git.getTree({...repo, tree_sha: ref, recursive: '1'})).data;
  if (tree.truncated) throw new Error('Storage scan was truncated; split the tree before proceeding.');
  const grandfathered = new Set(JSON.parse(process.env.STORAGE_EXISTING_LARGE_BLOBS || '[]'));
  const large = tree.tree.filter(f => f.type === 'blob' && f.size > 50 * 1024 * 1024 && !grandfathered.has(f.sha));
  if (large.length) throw new Error('New or changed Git files over 50 MiB: ' + large.map(f => f.path).join(', '));
  for (const file of tree.tree.filter(f => /^\.github\/workflows\/[^/]+\.ya?ml$/.test(f.path))) {
    const blob = (await github.rest.git.getBlob({...repo, file_sha: file.sha})).data;
    const text = Buffer.from(blob.content, 'base64').toString('utf8');
    if (/uses:\s*actions\/upload-artifact@/.test(text) && !(repo.repo === 'Portrait-PC-Camera' && file.path === '.github/workflows/build.yml' && /retention-days:\s*1\s/.test(text)))
      throw new Error('Persistent artifact upload reintroduced in ' + file.path + '; use publish-build.cjs.');
  }
  const releases = await github.paginate(github.rest.repos.listReleases, {...repo, per_page: 100});
  for (const r of releases) {
    const assets = await github.paginate(github.rest.repos.listReleaseAssets, {...repo, release_id: r.id, per_page: 100});
    if (context.eventName !== 'pull_request' && r.tag_name === 'ci-latest') {
      for (const a of assets.filter(a => /\.pending-/.test(a.name) && Date.now() - Date.parse(a.created_at) > 86400000))
        await remove(() => github.rest.repos.deleteReleaseAsset({...repo, asset_id: a.id}));
    }
    if (assets.some(a => a.size > 1900 * 1024 * 1024)) throw new Error('Release asset exceeds the 1900 MiB safety threshold: ' + r.tag_name);
    if (r.tag_name === 'ci-latest' && assets.reduce((n,a) => n+a.size,0) > 4 * 1024 ** 3)
      throw new Error('Rolling CI release exceeds 4 GiB.');
  }
  if (context.eventName !== 'pull_request') {
    const caches = await github.paginate(github.rest.actions.getActionsCacheList, {...repo, per_page: 100});
    let total = caches.reduce((n,c) => n+c.size_in_bytes,0);
    for (const c of caches.sort((a,b) => a.last_accessed_at.localeCompare(b.last_accessed_at))) {
      if (total <= 5 * 1024 ** 3) break;
      await remove(() => github.rest.actions.deleteActionsCacheById({...repo, cache_id: c.id}));
      total -= c.size_in_bytes;
    }
    const artifacts = await github.paginate(github.rest.actions.listArtifactsForRepo, {...repo, per_page: 100});
    for (const a of artifacts) {
      if (Date.now() - Date.parse(a.created_at) > 2 * 86400000)
        await remove(() => github.rest.actions.deleteArtifact({...repo, artifact_id: a.id}));
    }
    const retained = artifacts.filter(a => Date.now() - Date.parse(a.created_at) <= 2 * 86400000);
    if (retained.reduce((n,a) => n+a.size_in_bytes,0) > 100 * 1024 * 1024)
      throw new Error('Temporary Actions storage exceeds 100 MiB; use the rolling CI release for build downloads.');
    await core.summary.addRaw(`Storage policy checked. Cache: ${(total / 1024 ** 3).toFixed(2)} GiB; Git: ${(metadata.size / 1024).toFixed(1)} MiB.\n`).write();
  }
};
