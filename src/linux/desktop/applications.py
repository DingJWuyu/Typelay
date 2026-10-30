"""Native application-list management; membership never grants input authority."""

import gi
from design import apply_theme, box, design_icon, group, label, style

gi.require_version("Gtk", "3.0")
from gi.repository import Gdk, Gio, Gtk, Pango  # noqa: E402


class ApplicationsPane:
    """One page owns its catalogue, projection and retired GTK peers."""

    def __init__(self, owner):
        self.owner = owner
        self.box = box(0)
        self.catalog = None
        self.projection = None
        self.known = ()
        self.opening = object()
        self.rows_token = object()
        self.loading = False
        self.reload_requested = False
        self.pending = None
        self.dialog = None
        self.add_intent = None
        self.signature = None
        self.settings_signature = None
        self.toast_identity = None
        self.toast_message = None
        self.summary_signature = None
        self.rows = {}
        self.summary = group()
        self.box.pack_start(self.summary, False, False, 0)
        heading = style(label(), "group-title")
        heading.set_margin_top(20)
        heading.set_margin_bottom(8)
        self.heading = heading
        self.box.pack_start(heading, False, False, 0)
        self.list = group()
        self.list_rows = box(0)
        self.list.pack_start(self.list_rows, False, False, 0)
        toolbar = style(Gtk.Box(), "applications-listbar")
        self.add = style(Gtk.Button(), "flat")
        self.add.add(design_icon("plus", 16, symbolic=True))
        self.add.connect("clicked", self.pick)
        toolbar.pack_start(self.add, False, False, 0)
        self.list.pack_start(toolbar, False, False, 0)
        self.box.pack_start(self.list, False, False, 0)
        # Keep the settings owner interface used by existing public UI tests.
        owner.applications_box = self.list_rows
        owner.add_application = self.add
        owner.application_controls = {}
        self.box.connect("unmap", lambda *_: self.retire())
        self.box.connect("map", lambda *_: self.reenter())
        owner.pages.connect("notify::visible-child-name", self.page_changed)
        owner.window.connect("unmap", lambda *_: self.retire())

    def current(self, opening=None, *, active=False):
        return (
            (opening is None or opening is self.opening)
            and self.owner.window.get_mapped()
            and self.box.get_mapped()
            and self.owner.pages.get_visible_child_name() == "application-support"
            and (not active or self.owner.window.is_active())
        )

    def retire(self):
        self.opening = object()
        self.rows_token = object()
        self.signature = self.summary_signature = None
        self.close_dialog()

    def page_changed(self, *_):
        if self.owner.pages.get_visible_child_name() == "application-support":
            self.reenter()
        else:
            self.retire()

    def reenter(self):
        self.signature = self.summary_signature = None
        self.render()
        self.owner.tasks(self.refresh())

    def close_dialog(self):
        self.add_intent = None
        dialog, self.dialog = self.dialog, None
        if dialog is not None:
            dialog.destroy()

    def update(self):
        translation = getattr(self.owner, "translation", None)
        settings_signature = (
            repr(self.owner.value),
            getattr(self.owner, "last_application", None),
            repr((translation.style_state or {}).get("savedPreferences")) if translation else None,
        )
        if settings_signature != self.settings_signature:
            self.settings_signature = settings_signature
            # Invalidate peers immediately, before the fresh revision arrives.
            self.projection = None
            self.rows_token = object()
            self.signature = None
            self.close_dialog()
            self.owner.tasks(self.refresh())
        self.render()
        if self.toast_identity is self.owner.toast.identity and self.toast_message:
            self.owner.toast.text.set_text(self.owner.tr(*self.toast_message))
            self.owner.toast.undo.set_label(self.owner.tr("撤销", "Undo"))

    async def refresh(self):
        if self.loading:
            self.reload_requested = True
            return
        self.loading = True
        self.reload_requested = False
        opening, settings_signature = self.opening, self.settings_signature
        try:
            catalog = await self.owner.application_support.action_requested(
                "desktop-text", "ListManagedApplications"
            )
            if not isinstance(catalog, list):
                raise ValueError("applications-unavailable")
            known = tuple(
                dict.fromkeys(
                    [item["application"] for item in catalog if item.get("application")]
                    + (
                        [self.owner.last_application]
                        if getattr(self.owner, "last_application", None)
                        else []
                    )
                )
            )
            projection = await self.owner.host.ask(
                "getApplicationList", {"knownApplications": list(known)}, timeout=2
            )
            if (
                not isinstance(projection, dict)
                or type(projection.get("settingsRevision")) is not int
                or not isinstance(projection.get("applications"), list)
                or not all(isinstance(value, str) for value in projection["applications"])
            ):
                raise ValueError("application-list-unconfirmed")
            if opening is self.opening and settings_signature == self.settings_signature:
                self.catalog, self.known, self.projection = catalog, known, projection
                self.render()
        except (OSError, ValueError, EOFError, TimeoutError):
            if opening is self.opening:
                self.failure()
        finally:
            self.loading = False
            if self.reload_requested or opening is not self.opening:
                self.owner.tasks(self.refresh())

    def failure(self):
        self.owner.report_save_result(
            False,
            self.owner.tr(
                "设置已变化或保存未确认，请检查当前列表后重试。",
                "Settings changed or the save was not confirmed. Review the current list and try again.",
            ),
        )

    def message(self, chinese, english, undo=None):
        self.owner.toast.show_message(self.owner.tr(chinese, english), undo)
        self.toast_identity = self.owner.toast.identity
        self.toast_message = (chinese, english)

    def render(self):
        owner = self.owner
        self.heading.set_text(owner.tr("应用", "Apps"))
        self.add.get_accessible().set_name(owner.tr("添加应用", "Add app"))
        self.add.set_tooltip_text(owner.tr("添加应用", "Add app"))
        self.add.set_sensitive(self.projection is not None and self.pending is None)
        self.render_summary()
        state = owner.translation.style_state if hasattr(owner, "translation") else None
        signature = (
            repr(self.projection),
            repr(owner.value),
            repr(state),
            self.pending is not None,
        )
        if signature == self.signature:
            return
        self.signature = signature
        self.rows_token = object()
        token, opening = self.rows_token, self.opening
        self.rows = {}
        owner.application_controls = {}
        for child in self.list_rows.get_children():
            child.destroy()
        applications = (self.projection or {}).get("applications", [])
        if not applications:
            empty = style(
                label(
                    owner.tr(
                        "暂无应用，点击 + 添加。" if self.projection else "正在读取应用…",
                        "No apps yet. Click + to add one." if self.projection else "Loading apps…",
                    )
                ),
                "applications-empty",
                "dim-label",
            )
            self.list_rows.pack_start(empty, False, False, 0)
        catalog = {
            item["application"].casefold(): item
            for item in self.catalog or []
            if item.get("application")
        }
        excluded = {
            item.casefold() for item in ((owner.value or {}).get("excludedApplications") or [])
        }
        styles = {
            key.casefold(): value
            for key, value in (
                ((state or {}).get("savedPreferences") or {}).get("applications") or {}
            ).items()
        }
        revision = (self.projection or {}).get("settingsRevision")
        for application in applications:
            item = catalog.get(
                application.casefold(), {"name": application, "application": application}
            )
            row = style(Gtk.EventBox(), "application-row")
            row.set_visible_window(False)
            row.set_can_focus(True)
            row.get_accessible().set_name(owner.tr("应用：", "App: ") + item["name"])
            line = style(Gtk.Box(spacing=12), "application-line")
            row.add(line)
            line.pack_start(self.icon(item), False, False, 0)
            name = label(item["name"])
            name.typelay_user_content = True
            name.set_ellipsize(Pango.EllipsizeMode.END)
            line.pack_start(name, True, True, 0)
            enabled = Gtk.Switch()
            enabled.set_valign(Gtk.Align.CENTER)
            enabled.set_active(application.casefold() not in excluded)
            enabled.get_accessible().set_name(
                owner.tr("在应用中启用：", "Enable in app: ") + item["name"]
            )
            enabled.set_tooltip_text(
                owner.tr(
                    "即时保存；不改变应用连接权限",
                    "Saved immediately; app connection permissions are unchanged.",
                )
            )
            removal = next(
                (
                    entry.get("removal", {}).get("canRemove")
                    for entry in (state or {}).get("applications", [])
                    if entry["application"].casefold() == application.casefold()
                ),
                False,
            )
            can_change = bool((state or {}).get("isReleased") and (state or {}).get("canChange"))
            choices = Gtk.ListStore(str, str, bool)
            for identity, zh, en in (
                ("inherit", "默认风格", "Default tone"),
                ("Natural", "自然", "Natural"),
                ("Formal", "正式", "Formal"),
                ("Concise", "简洁 · 尚未提供", "Concise · Not available"),
                ("Casual", "口语 · 尚未提供", "Casual · Not available"),
            ):
                choices.append(
                    (
                        identity,
                        owner.tr(zh, en),
                        (can_change or identity == "inherit" and bool(removal))
                        and identity not in ("Concise", "Casual"),
                    )
                )
            tone = style(Gtk.ComboBox.new_with_model(choices), "application-tone")
            tone.set_valign(Gtk.Align.CENTER)
            tone.set_id_column(0)
            renderer = Gtk.CellRendererText()
            tone.pack_start(renderer, True)
            tone.add_attribute(renderer, "text", 1)
            tone.add_attribute(renderer, "sensitive", 2)
            tone.set_active_id(styles.get(application.casefold(), "inherit"))
            tone.set_sensitive(
                enabled.get_active() and self.pending is None and (can_change or bool(removal))
            )
            tone.get_accessible().set_name(owner.tr("应用风格：", "App tone: ") + item["name"])
            tone.set_tooltip_text(
                owner.tr(
                    "保留已保存的覆盖；当前未开放的风格不可选择。",
                    "Saved overrides are retained. Unreleased tones cannot be selected.",
                )
            )
            enabled.set_sensitive(self.pending is None)
            self.rows[application.casefold()] = row
            owner.application_controls[application.casefold()] = (enabled, tone)

            def valid(
                peer=row,
                key=application.casefold(),
                epoch=token,
                session=opening,
                expected=revision,
            ):
                return self.row_current(peer, key, epoch, session, expected)

            enabled.connect(
                "notify::active",
                lambda control, _, app=application, guard=valid, expected=revision: self.intent(
                    guard, self.set_enabled, app, control.get_active(), expected
                ),
            )
            tone.connect(
                "changed",
                lambda control, app=application, guard=valid, expected=revision: self.tone_intent(
                    control, guard, app, expected
                ),
            )
            row.connect(
                "button-press-event",
                lambda _row, event, app=application, entry=item, guard=valid: self.context(
                    event, app, entry, guard
                ),
            )
            row.connect(
                "popup-menu",
                lambda _row, app=application, entry=item, guard=valid: self.confirm_remove(
                    app, entry, guard
                ),
            )
            row.connect(
                "key-press-event",
                lambda _row, event, app=application, entry=item, guard=valid: self.key_menu(
                    event, app, entry, guard
                ),
            )
            line.pack_start(tone, False, False, 0)
            line.pack_end(enabled, False, False, 0)
            self.list_rows.pack_start(row, False, False, 0)
        self.list_rows.show_all()

    def row_current(self, row, key, token, opening, revision):
        return (
            self.current(opening, active=True)
            and self.pending is None
            and token is self.rows_token
            and self.rows.get(key) is row
            and row.get_parent() is self.list_rows
            and row.get_mapped()
            and row.is_sensitive()
            and self.projection is not None
            and self.projection["settingsRevision"] == revision
        )

    def intent(self, guard, action, *args):
        if guard():
            self.owner.tasks(action(*args, opening=self.opening, guard=guard))

    def tone_intent(self, control, guard, application, revision):
        if not guard() or not control.get_mapped() or not control.is_sensitive():
            return
        selected = control.get_active_iter()
        if selected is None or not control.get_model()[selected][2]:
            return
        self.intent(guard, self.set_tone, application, control.get_active_id(), revision)

    @staticmethod
    def icon(item):
        icon = Gtk.Image.new_from_icon_name("application-x-executable", Gtk.IconSize.MENU)
        if item.get("id"):
            try:
                entry = Gio.DesktopAppInfo.new(item["id"])
                if entry and entry.get_icon():
                    icon.set_from_gicon(entry.get_icon(), Gtk.IconSize.MENU)
            except TypeError:
                pass
        icon.set_pixel_size(18)
        icon.set_size_request(24, 24)
        return icon

    def context(self, event, application, item, guard):
        return self.confirm_remove(application, item, guard) if event.button == 3 else False

    def key_menu(self, event, application, item, guard):
        if (
            event.keyval == Gdk.KEY_Menu
            or event.keyval == Gdk.KEY_F10
            and event.state & Gdk.ModifierType.SHIFT_MASK
        ):
            return self.confirm_remove(application, item, guard)
        return False

    def make_dialog(self, title, width):
        self.close_dialog()
        dialog = Gtk.Dialog(title=title, transient_for=self.owner.window, modal=True)
        dialog.set_resizable(False)
        dialog.set_default_size(width, -1)
        style(dialog, "applications-dialog")
        apply_theme(dialog, self.owner.value or {})
        content = dialog.get_content_area()
        content.set_border_width(22)
        content.set_spacing(14)
        content.pack_start(style(label(title), "applications-dialog-title"), False, False, 0)
        dialog.add_button(self.owner.tr("取消", "Cancel"), Gtk.ResponseType.CANCEL)
        dialog.set_default_response(Gtk.ResponseType.CANCEL)
        self.dialog = dialog
        dialog.connect("destroy", lambda widget: self.dialog_closed(widget))
        return dialog

    def dialog_closed(self, dialog):
        if self.dialog is dialog:
            self.dialog = None

    def confirm_remove(self, application, item, guard):
        if not guard():
            return False
        opening, revision, token = (
            self.opening,
            self.projection["settingsRevision"],
            self.rows_token,
        )
        known = self.known
        dialog = self.make_dialog(
            self.owner.tr("移除「{app}」？", "Remove {app}?").format(app=item["name"]), 340
        )
        note = label(
            self.owner.tr(
                "仅从列表移除，停用与风格设置保留。",
                "Remove only from this list. Disabled-app and tone settings are retained.",
            ),
            muted=True,
        )
        note.set_line_wrap(True)
        note.set_max_width_chars(36)
        dialog.get_content_area().pack_start(note, False, False, 0)
        style(
            dialog.add_button(self.owner.tr("移除", "Remove"), Gtk.ResponseType.OK),
            "suggested-action",
        )
        answered = False

        def response(widget, result):
            nonlocal answered
            if answered or self.dialog is not widget or not widget.get_mapped():
                return
            answered = True
            valid = (
                self.current(opening)
                and widget.is_active()
                and token is self.rows_token
                and self.projection is not None
                and self.projection["settingsRevision"] == revision
            )
            widget.destroy()
            if valid and result == Gtk.ResponseType.OK:
                self.owner.tasks(
                    self.membership(application, False, revision, opening=opening, known=known)
                )

        dialog.connect("response", response)
        dialog.show_all()
        return True

    def pick(self, button):
        if (
            button is not self.add
            or not button.get_mapped()
            or not button.is_sensitive()
            or not self.current(active=True)
            or self.pending is not None
            or self.projection is None
        ):
            return
        opening, revision, known = self.opening, self.projection["settingsRevision"], self.known
        dialog = self.make_dialog(self.owner.tr("添加应用", "Add app"), 320)
        intent = object()
        self.add_intent = intent
        selected = False

        def abandoned(_widget):
            if not selected and self.add_intent is intent:
                self.add_intent = None

        dialog.connect("destroy", abandoned)
        search_box = Gtk.Overlay()
        search = style(Gtk.SearchEntry(), "applications-find")
        search.get_accessible().set_name(self.owner.tr("搜索应用", "Search apps"))
        search_box.add(search)
        hint = style(label(self.owner.tr("搜索", "Search")), "dim-label")
        hint.set_margin_start(30)
        hint.set_halign(Gtk.Align.START)
        hint.set_valign(Gtk.Align.CENTER)
        hint.set_no_show_all(True)
        search_box.add_overlay(hint)
        search_box.set_overlay_pass_through(hint, True)
        dialog.get_content_area().pack_start(search_box, False, False, 0)
        choices = box(0)
        scroll = Gtk.ScrolledWindow()
        scroll.set_policy(Gtk.PolicyType.NEVER, Gtk.PolicyType.AUTOMATIC)
        scroll.set_max_content_height(260)
        scroll.set_propagate_natural_height(True)
        scroll.add(choices)
        dialog.get_content_area().pack_start(scroll, True, True, 0)
        generation = object()
        included = {value.casefold() for value in self.projection["applications"]}

        def live():
            return (
                self.dialog is dialog
                and self.add_intent is intent
                and self.current(opening)
                and dialog.get_mapped()
                and dialog.is_active()
                and self.projection is not None
                and self.projection["settingsRevision"] == revision
                and self.pending is None
            )

        def render(*_):
            nonlocal generation
            generation = object()
            current_generation = generation
            query = search.get_text().strip().casefold()
            hint.set_visible(not query)
            for child in choices.get_children():
                child.destroy()
            entries = [
                item
                for item in self.catalog or []
                if item.get("application", "").casefold() not in included
                and query in (item["name"] + " " + item.get("application", "")).casefold()
            ]
            for item in entries:
                option = style(Gtk.Button(), "apps-option")
                line = Gtk.Box(spacing=10)
                line.pack_start(self.icon(item), False, False, 0)
                line.pack_start(label(item["name"]), True, True, 0)
                option.add(line)
                option.get_accessible().set_name(item["name"])
                option.set_sensitive(bool(item.get("application")))
                if item.get("unavailableReason"):
                    option.set_tooltip_text(self.owner.localizer.text(item["unavailableReason"]))

                def choose(peer, entry=item, epoch=current_generation):
                    nonlocal selected
                    if (
                        not live()
                        or epoch is not generation
                        or peer.get_parent() is not choices
                        or not peer.get_mapped()
                        or not peer.is_sensitive()
                    ):
                        return
                    selected = True
                    dialog.destroy()
                    self.owner.tasks(self.add_registered(entry, revision, opening, known, intent))

                option.connect("clicked", choose)
                choices.pack_start(option, False, False, 0)
            if not entries:
                choices.pack_start(
                    label(self.owner.tr("没有匹配的应用", "No matching apps"), muted=True),
                    False,
                    False,
                    8,
                )
            choices.show_all()

        search.connect("changed", render)
        dialog.connect("response", lambda widget, _response: widget.destroy())
        render()
        dialog.show_all()
        hint.set_visible(not search.get_text())
        search.grab_focus()

    async def add_registered(self, entry, revision, opening, known, intent):
        if not self.current(opening) or self.pending is not None or self.add_intent is not intent:
            return
        try:
            identity = await self.owner.application_support.action_requested(
                "desktop-text", "ResolveApplicationExclusion", desktop_id=entry["id"]
            )
            if (
                not self.current(opening)
                or self.add_intent is not intent
                or identity != entry.get("application")
            ):
                return
            self.add_intent = None
            await self.membership(identity, True, revision, opening=opening, known=known)
        except (OSError, ValueError, EOFError, TimeoutError):
            if self.current(opening) and self.add_intent is intent:
                self.add_intent = None
                self.failure()

    async def membership(self, application, included, revision, *, opening, known, undo=False):
        await self.commit(
            "changeApplicationList",
            {
                "application": application,
                "included": included,
                "expectedSettingsRevision": revision,
                "knownApplications": list(known),
            },
            opening,
            (
                ("已添加应用，保留原有设置", "App added; existing settings retained")
                if included
                else (
                    "已从列表移除，原有设置保留",
                    "App removed from the list; existing settings retained",
                )
            ),
            None
            if included or undo
            else lambda saved_revision: self.owner.tasks(
                self.membership(
                    application, True, saved_revision, opening=opening, known=known, undo=True
                )
            ),
        )

    async def set_enabled(self, application, enabled, revision, *, opening, guard=None):
        if guard is not None and not guard():
            return
        await self.commit(
            "applicationEnabled",
            {
                "application": application,
                "enabled": enabled,
                "expectedSettingsRevision": revision,
            },
            opening,
            ("已启用此应用", "App enabled") if enabled else ("已停用此应用", "App disabled"),
            None
            if enabled
            else lambda saved_revision: self.owner.tasks(
                self.set_enabled(application, True, saved_revision, opening=opening)
            ),
        )

    async def set_tone(self, application, tone, revision, *, opening, guard):
        if tone not in ("inherit", "Natural", "Formal") or not guard():
            return
        try:
            state = await self.owner.host.ask("getApplicationStyleSettings", application, timeout=2)
            if (
                not guard()
                or not isinstance(state, dict)
                or state.get("settingsRevision") != revision
            ):
                return
            await self.commit(
                "changeApplicationStyle",
                {
                    "application": application,
                    "style": None if tone == "inherit" else tone,
                    "expectedSettingsRevision": revision,
                },
                opening,
                ("应用风格已保存", "App tone saved"),
            )
        except (OSError, ValueError, EOFError, TimeoutError):
            if self.current(opening):
                self.failure()

    async def commit(self, operation, payload, opening, message, undo=None):
        if not self.current(opening) or self.pending is not None:
            return
        request = object()
        self.pending = request
        self.render()
        try:
            result = await self.owner.host.ask(operation, payload, timeout=3)
            if not self.current(opening):
                return
            saved = isinstance(result, dict) and result.get("saved") is True
            self.owner.report_save_result(
                saved,
                self.owner.tr(
                    "设置已变化或保存失败，请检查当前列表后重试。",
                    "Settings changed or could not be saved. Review the current list and try again.",
                ),
            )
            if saved:
                if undo is not None:
                    revision = result["settingsRevision"]
                    self.message(*message, lambda: undo(revision))
                else:
                    self.message(*message)
        except (OSError, ValueError, EOFError, TimeoutError):
            if self.current(opening):
                self.failure()
        finally:
            if self.pending is request:
                self.pending = None
            self.signature = None
            self.owner.translation.refresh()
            await self.refresh()

    def render_summary(self):
        support, owner = self.owner.application_support, self.owner
        facts = (support.value or {}).get("rows", [])
        signature = (repr(facts), support.busy, owner.tr("中文", "English"))
        if signature == self.summary_signature:
            return
        self.summary_signature = signature
        for child in self.summary.get_children():
            child.destroy()
        desktop = next((row for row in facts if row["id"] == "desktop-text"), None)
        browsers = [row for row in facts if row["id"] != "desktop-text"]
        # Prefer a live connection; otherwise present the first actual browser
        # fact. No aggregate connected/installed state is invented.
        browser = next(
            (row for row in browsers if row.get("state") in ("Connected", "BuiltInAvailable")),
            browsers[0] if browsers else None,
        )
        for row, zh, en, symbol in (
            (desktop, "桌面应用", "Desktop apps", "keyboard"),
            (browser, "浏览器扩展", "Browser extension", "globe"),
        ):
            line = style(Gtk.Box(spacing=12), "application-line")
            mark = style(
                Gtk.EventBox(),
                "application-connection-icon",
                "desktop-icon" if symbol == "keyboard" else "browser-icon",
            )
            mark.add(design_icon(symbol, 15))
            line.pack_start(mark, False, False, 0)
            line.pack_start(label(owner.tr(zh, en)), True, True, 0)
            status = (
                owner.localizer.text(row["statusText"])
                if row
                else owner.tr("尚未检查", "Not checked")
            )
            state = row.get("state") if row else None
            dot = style(Gtk.Box(), "applications-state-dot")
            dot.set_valign(Gtk.Align.CENTER)
            if state in ("Connected", "BuiltInAvailable"):
                style(dot, "connected")
            elif state in ("Conflict", "NeedsInstall", "Unavailable", "Failed"):
                style(dot, "attention")
            line.pack_start(dot, False, False, 0)
            caption = label(status, muted=True)
            line.pack_start(caption, False, False, 0)
            action = row.get("primaryAction") if row else None
            button = Gtk.Button(
                label=owner.localizer.text(action["label"]) if action else owner.tr("查看", "View")
            )
            button.set_sensitive(not support.busy)
            button.get_accessible().set_name(
                owner.tr("应用连接：", "App connection: ") + owner.tr(zh, en)
            )
            opening = self.opening

            def activate(peer, fact=row, intent=action, session=opening):
                if (
                    not self.current(session, active=True)
                    or not peer.get_mapped()
                    or not peer.is_sensitive()
                    or peer.get_parent() not in self.summary.get_children()
                ):
                    return
                if fact is None or intent is None:
                    support.connection_details.set_expanded(True)
                    if fact:
                        support.selector.set_active_id(fact["id"])
                    owner.tasks(support.refresh())
                    return

                def guard():
                    return (
                        self.current(session)
                        and support.rows.get(fact["id"]) == fact
                        and (support.rows.get(fact["id"]) or {}).get("primaryAction") == intent
                    )

                support.activate(fact, intent, guard=guard)

            button.connect("clicked", activate)
            line.pack_end(button, False, False, 0)
            self.summary.pack_start(line, False, False, 0)
        self.summary.show_all()
