package com.exerussus.salamander.rider.newfile;

import com.intellij.openapi.project.Project;
import com.intellij.openapi.ui.DialogWrapper;
import com.intellij.openapi.ui.ValidationInfo;
import com.intellij.openapi.vfs.VirtualFile;
import org.jetbrains.annotations.Nullable;

import javax.swing.BorderFactory;
import javax.swing.Box;
import javax.swing.BoxLayout;
import javax.swing.JCheckBox;
import javax.swing.JComponent;
import javax.swing.JLabel;
import javax.swing.JPanel;
import javax.swing.JScrollPane;
import javax.swing.JTextField;
import java.awt.BorderLayout;
import java.awt.Component;
import java.awt.Dimension;
import java.util.ArrayList;
import java.util.List;
import java.util.regex.Pattern;

/**
 * Имя + галочки: какие константы вписать и какие события завести.
 * Обязательные константы отмечены и заблокированы — без них не соберётся.
 */
final class NewFileDialog extends DialogWrapper {
    private static final Pattern IDENT = Pattern.compile("[A-Za-z_][A-Za-z0-9_]*");

    private final Template template;
    private final VirtualFile dir;
    private final JTextField name = new JTextField(28);
    private final List<JCheckBox> constBoxes = new ArrayList<>();
    private final List<JCheckBox> eventBoxes = new ArrayList<>();

    NewFileDialog(@Nullable Project project, Template template, VirtualFile dir) {
        super(project);
        this.template = template;
        this.dir = dir;
        setTitle("New Salamander " + template.title());
        init();
    }

    @Override
    public @Nullable JComponent getPreferredFocusedComponent() {
        return name;
    }

    @Override
    protected @Nullable JComponent createCenterPanel() {
        JPanel top = new JPanel(new BorderLayout(8, 4));
        String label = template.kind == Template.Kind.ARCHETYPE ? "Id (и имя файла):" : "Имя:";
        top.add(new JLabel(label), BorderLayout.WEST);
        top.add(name, BorderLayout.CENTER);
        String about = template.description();
        if (!about.isEmpty()) top.add(new JLabel("<html><div style='width:420px'>" + esc(about) + "</div></html>"), BorderLayout.SOUTH);

        JPanel list = new JPanel();
        list.setLayout(new BoxLayout(list, BoxLayout.Y_AXIS));

        List<ApiModel.Const> consts = template.consts();
        if (!consts.isEmpty()) {
            list.add(header("Константы"));
            for (ApiModel.Const c : consts) {
                JCheckBox box = new JCheckBox(c.type() + " " + c.name() + " = " + template.value(c));
                box.setToolTipText(tip(c.doc()));
                if (c.required()) {
                    box.setSelected(true);
                    box.setEnabled(false);
                    box.setText(box.getText() + "   (обязательна)");
                }
                box.setAlignmentX(Component.LEFT_ALIGNMENT);
                constBoxes.add(box);
                list.add(box);
            }
        }

        List<ApiModel.Event> events = template.events();
        if (!events.isEmpty()) {
            if (!consts.isEmpty()) list.add(Box.createVerticalStrut(8));
            list.add(header(template.kind == Template.Kind.ARCHETYPE ? "События" : "События хоста"));
            for (int i = 0; i < events.size(); i++) {
                ApiModel.Event e = events.get(i);
                JCheckBox box = new JCheckBox(signature(e));
                box.setToolTipText(tip(e.summary()));
                // архетипу без событий не собраться — первое отмечаем сразу
                if (i == 0 && template.kind == Template.Kind.ARCHETYPE && template.needsEvent()) box.setSelected(true);
                box.setAlignmentX(Component.LEFT_ALIGNMENT);
                eventBoxes.add(box);
                list.add(box);
            }
        }

        JPanel root = new JPanel(new BorderLayout(0, 8));
        root.add(top, BorderLayout.NORTH);
        if (list.getComponentCount() > 0) {
            JScrollPane scroll = new JScrollPane(list);
            scroll.setBorder(BorderFactory.createEmptyBorder());
            scroll.getVerticalScrollBar().setUnitIncrement(16);
            int h = Math.min(420, 26 * list.getComponentCount() + 16);
            scroll.setPreferredSize(new Dimension(560, h));
            root.add(scroll, BorderLayout.CENTER);
        }
        return root;
    }

    @Override
    protected @Nullable ValidationInfo doValidate() {
        String n = name.getText().trim();
        if (n.isEmpty()) return new ValidationInfo("Введите имя", name);
        if (!IDENT.matcher(n).matches()) return new ValidationInfo("Только латиница, цифры и _; с цифры начинать нельзя", name);
        if (dir.findChild(n + ".sal") != null) return new ValidationInfo("Файл " + n + ".sal уже есть", name);
        if (template.needsEvent() && selectedEvents().isEmpty())
            return new ValidationInfo("Отметьте хотя бы одно событие: без него " + template.title() + " не соберётся");
        return null;
    }

    String fileName() {
        return name.getText().trim();
    }

    List<ApiModel.Const> selectedConsts() {
        List<ApiModel.Const> out = new ArrayList<>();
        List<ApiModel.Const> all = template.consts();
        for (int i = 0; i < all.size(); i++) if (constBoxes.get(i).isSelected()) out.add(all.get(i));
        return out;
    }

    List<ApiModel.Event> selectedEvents() {
        List<ApiModel.Event> out = new ArrayList<>();
        List<ApiModel.Event> all = template.events();
        for (int i = 0; i < all.size(); i++) if (eventBoxes.get(i).isSelected()) out.add(all.get(i));
        return out;
    }

    private static JLabel header(String text) {
        JLabel l = new JLabel("<html><b>" + text + "</b></html>");
        l.setAlignmentX(Component.LEFT_ALIGNMENT);
        return l;
    }

    private static String signature(ApiModel.Event e) {
        StringBuilder sb = new StringBuilder(e.name()).append('(');
        for (int i = 0; i < e.params().size(); i++) {
            if (i > 0) sb.append(", ");
            sb.append(e.params().get(i).type()).append(' ').append(e.params().get(i).name());
        }
        return sb.append(')').toString();
    }

    private static @Nullable String tip(String doc) {
        if (doc == null || doc.isBlank()) return null;
        return "<html><div style='width:360px'>" + esc(doc).replace("\n", "<br>") + "</div></html>";
    }

    private static String esc(String s) {
        return s.replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;");
    }
}
