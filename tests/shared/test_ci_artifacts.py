"""Keep the real workflow's artifact uploads complete and payload-free, offline."""

import tempfile
import unittest
from pathlib import Path

import yaml
from ci_artifact_workflow import selected_files, verify_workflow

ROOT = Path(__file__).resolve().parents[2]
WORKFLOW = ROOT / ".github/workflows/check.yml"


def upload_step(workflow, prefix, job="linux"):
    return next(
        step
        for step in workflow["jobs"][job]["steps"]
        if step.get("with", {}).get("name", "").startswith(prefix + "-${{ github.sha }}")
    )


class ArtifactUploadTests(unittest.TestCase):
    def reject_change(self, change, message):
        workflow = yaml.safe_load(WORKFLOW.read_text(encoding="utf-8"))
        change(workflow)
        with tempfile.TemporaryDirectory() as temporary:
            changed = Path(temporary) / "check.yml"
            changed.write_text(yaml.safe_dump(workflow), encoding="utf-8")
            with self.assertRaisesRegex(AssertionError, message):
                verify_workflow(changed)
        verify_workflow(WORKFLOW)

    def test_file_selection_matches_pinned_action_directory_and_hidden_rules(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            samples = (
                "artifacts/result.json",
                "artifacts/nested/result.json",
                "artifacts/report.json/child.log",
                "artifacts/.hidden.json",
                "artifacts/.cache/result.json",
            )
            for sample in samples:
                path = root / sample
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_text("synthetic", encoding="utf-8")
            file_paths = tuple(
                path.relative_to(root).as_posix() for path in root.rglob("*") if path.is_file()
            )
            cases = (
                ("artifacts/result.json/", False, set()),
                ("artifacts/*.json", False, {samples[0], samples[2]}),
                ("artifacts/*.json/", False, {samples[2]}),
                ("artifacts/nested/", False, {samples[1]}),
                ("artifacts/result.json\nartifacts/result.json", False, {samples[0]}),
                ("artifacts/RESULT.JSON", True, {samples[0]}),
                ("artifacts/RESULT.JSON", False, set()),
                ("artifacts/missing.json", False, set()),
                ("artifacts/", False, set(samples[:3])),
            )
            for pattern, windows, expected in cases:
                with self.subTest(pattern=pattern, windows=windows):
                    self.assertEqual(selected_files(file_paths, pattern, windows), expected)

    def test_real_workflow_preserves_each_artifact_in_every_matrix_group(self):
        verify_workflow(WORKFLOW)

    def test_shared_upload_requires_all_four_default_named_trx_results(self):
        def change(workflow):
            upload_step(workflow, "shared", "shared")["with"]["path"] = (
                "artifacts/ci/\nartifacts/shared-test-results/core.trx"
            )

        self.reject_change(change, "Missing evidence.*shared-test-results/_")
        results = (
            "artifacts/shared-test-results/_runner_2026-11-17_06_06_28_net10.0.trx",
            "artifacts/shared-test-results/_runner_2026-11-17_06_06_30_net10.0.trx",
            "artifacts/shared-test-results/_runner_2026-11-17_06_06_44_net10.0.trx",
            "artifacts/shared-test-results/_runner_2026-11-17_06_06_46_net10.0.trx",
        )
        for missing in results:
            with self.subTest(missing=missing):
                self.reject_change(
                    lambda workflow: upload_step(workflow, "shared", "shared")["with"].update(
                        path="\n".join(("artifacts/ci/", *(p for p in results if p != missing)))
                    ),
                    "Missing evidence.*" + missing,
                )

    def test_missing_nested_evidence_fails_then_restored_workflow_passes(self):
        def change(workflow):
            options = upload_step(workflow, "linux-${{ matrix.group }}")["with"]
            options["path"] = options["path"].replace(
                "artifacts/linux/gtk4-desktop/gtk4-connected-before-map/*.json\n", ""
            )

        self.reject_change(change, "Missing evidence.*gtk4-connected-before-map/result.json")

    def test_payload_in_diagnostics_fails_then_restored_workflow_passes(self):
        def change(workflow):
            upload_step(workflow, "linux-${{ matrix.group }}")["with"]["path"] += (
                "\nartifacts/linux/"
            )

        self.reject_change(change, "Payload included")

    def test_active_model_and_runtime_caches_cannot_enter_diagnostics(self):
        for cache in (
            "hy-model-cache",
            "hy-cpu-download",
            "index-cpu-download",
            "index-cpu-runtime",
            "desktop-cache",
            "ordinary-gimp-cache",
            "uv-download",
        ):
            with self.subTest(cache=cache):
                self.reject_change(
                    lambda workflow: upload_step(workflow, "linux-${{ matrix.group }}")[
                        "with"
                    ].update(
                        path=upload_step(workflow, "linux-${{ matrix.group }}")["with"]["path"]
                        + f"\nartifacts/linux/{cache}/"
                    ),
                    "Payload included",
                )

    def test_wrong_upload_group_fails_then_restored_workflow_passes(self):
        def change(workflow):
            upload_step(workflow, "typelay-linux-gtk4-events")["if"] = (
                "${{ always() && matrix.group == 'native-gtk' }}"
            )

        self.reject_change(change, "Upload condition")

    def test_removed_package_opt_in_fails_then_restored_workflow_passes(self):
        def change(workflow):
            upload_step(workflow, "linux-package")["if"] = "matrix.group == 'native'"

        self.reject_change(change, "Upload condition")

    def test_small_artifact_cannot_borrow_required_files_from_comprehensive_upload(self):
        def change(workflow):
            options = upload_step(workflow, "typelay-linux-gimp")["with"]
            options["path"] = options["path"].replace(
                "artifacts/linux/ordinary-gimp/*.failed\n", ""
            )

        self.reject_change(change, "Missing evidence.*scenario.failed")

    def test_producer_moving_group_requires_its_upload_to_be_reviewed(self):
        def change(workflow):
            producer = next(
                step
                for step in workflow["jobs"]["linux"]["steps"]
                if step.get("id") == "gtk4-desktop"
            )
            producer["if"] = producer["if"].replace("'native-gtk4'", "'native-gtk'")

        self.reject_change(change, "Producer group")

    def test_fcitx_scope_must_consume_the_actual_matrix_group(self):
        def change(workflow):
            producer = next(
                step for step in workflow["jobs"]["linux"]["steps"] if step.get("id") == "fcitx"
            )
            producer["env"]["CI_LINUX_GROUP"] = "native-gtk"

        self.reject_change(change, "Unsupported fcitx production scope")

    def test_upload_before_its_producer_fails(self):
        def change(workflow):
            steps = workflow["jobs"]["linux"]["steps"]
            upload = upload_step(workflow, "typelay-linux-gimp")
            steps.remove(upload)
            steps.insert(0, upload)

        self.reject_change(change, "Upload precedes producer")

    def test_success_only_upload_loses_failure_diagnostics(self):
        def change(workflow):
            step = upload_step(workflow, "windows-trx-${{ matrix.shard }}", "windows-contracts")
            step["if"] = "success()"

        self.reject_change(change, "Upload condition")

    def test_diagnostics_survive_each_referenced_producers_non_success_outcomes(self):
        for producer in ("gimp", "gtk4-desktop"):
            for outcome in ("failure", "skipped", "cancelled"):
                with self.subTest(producer=producer, outcome=outcome):
                    self.reject_change(
                        lambda workflow: upload_step(workflow, "linux-${{ matrix.group }}").update(
                            {
                                "if": f"${{{{ always() && steps.{producer}.outcome != '{outcome}' }}}}"
                            }
                        ),
                        "Upload condition",
                    )

    def test_new_upload_dependency_combination_requires_explicit_coverage(self):
        self.reject_change(
            lambda workflow: upload_step(workflow, "linux-${{ matrix.group }}").update(
                {
                    "if": "${{ always() && (steps.gimp.outcome == 'success' || steps.qt.outcome == 'success') }}"
                }
            ),
            "Upload condition",
        )

    def test_new_upload_and_matrix_group_need_explicit_coverage(self):
        def add_upload(workflow):
            workflow["jobs"]["shared"]["steps"].append(
                {
                    "uses": "actions/upload-artifact@v4",
                    "with": {"name": "new-evidence", "path": "artifacts/"},
                }
            )

        self.reject_change(add_upload, "Uncovered uploads")
        self.reject_change(
            lambda workflow: workflow["jobs"]["linux-native"]["strategy"]["matrix"]["group"].append(
                "native-new"
            ),
            "Uncovered matrix groups",
        )

    def test_unknown_glob_if_and_upload_options_fail_closed(self):
        for pattern in (
            "artifacts/**/*.json",
            "!artifacts/package/",
            "artifacts/[ab].json",
            "artifacts/?.json",
            "${{ github.workspace }}/artifacts/",
        ):
            with self.subTest(pattern=pattern):
                self.reject_change(
                    lambda workflow: upload_step(workflow, "linux-${{ matrix.group }}")[
                        "with"
                    ].update(path=pattern),
                    "Unsupported upload glob",
                )
        self.reject_change(
            lambda workflow: upload_step(workflow, "linux-package").update(
                {"if": "contains(matrix.group, 'native')"}
            ),
            "Unsupported if",
        )
        self.reject_change(
            lambda workflow: upload_step(workflow, "linux-package")["with"].update(
                {"include-hidden-files": True}
            ),
            "Unsupported upload options",
        )

    def test_python_lexical_forms_are_not_actions_if_syntax(self):
        for expression in (
            "always() and True",
            "${{ always() && None == None }}",
            "always() or False",
            "always() && 1 == 1",
            'always() && "success" == "success"',
            "always() && 'true''true' == 'true''true'",
            "always() && 22 == 22",
        ):
            with self.subTest(expression=expression):
                self.reject_change(
                    lambda workflow: upload_step(workflow, "shared", "shared").update(
                        {"if": expression}
                    ),
                    "Unsupported if",
                )


if __name__ == "__main__":
    unittest.main()
