#!/usr/bin/env perl
# Shared Write, Edit, and apply_patch hook logic. Claude sends structured file fields. Codex sends the
# complete apply_patch program in tool_input.command. This helper normalizes both forms before applying
# the engine guards.
use strict;
use warnings;
use utf8;

use Cwd qw(getcwd abs_path);
use File::Basename qw(basename dirname);
use File::Spec;
use IPC::Open3;
use JSON::PP qw(decode_json encode_json);
use Symbol qw(gensym);

binmode STDIN, ':raw';
binmode STDOUT, ':raw';
binmode STDERR, ':encoding(UTF-8)';

my $phase = shift // '';
exit 0 unless $phase eq 'pre' || $phase eq 'post';

my $input_text = do { local $/; <STDIN> };
my $input = eval { decode_json($input_text) };
exit 0 unless ref $input eq 'HASH';

my $cwd = getcwd();
my $root = git_output($cwd, 'rev-parse', '--show-toplevel');
exit 0 unless defined $root && length $root;
$root = File::Spec->canonpath($root);

my ($changes, $parse_error) = normalize_changes($input, $cwd, $root, $phase);
if ($parse_error) {
    deny("The pending patch could not be checked safely: $parse_error") if $phase eq 'pre';
    exit 0;
}

if ($phase eq 'post') {
    version_changelog_reminder($changes);
    exit 0;
}

for my $change (@$changes) {
    next if $change->{kind} eq 'delete';
    my $rel = $change->{target_rel};
    next unless defined $rel && $rel =~ /\.(?:md|cs)\z/;
    if (($change->{added} // '') =~ /[—–]/) {
        deny('Introduces an em-dash or en-dash. Repo bans them in shipped text. Use a period, comma, colon, or parentheses instead.');
        exit 0;
    }
}

for my $change (@$changes) {
    next if $change->{kind} eq 'delete';
    my $rel = $change->{target_rel} // '';
    if ($rel eq 'docs/TODO.md' || $rel eq 'docs/ROADMAP.md') {
        deny('docs/TODO.md and docs/ROADMAP.md are retired: the backlog moved to GitHub Issues on 2026-07-17 and both files were deleted. Do not re-create them. File backlog work with: gh issue create --label kind/backlog --label confidence/lead. Search prior art first with scripts/ledger.sh search <term>.');
        exit 0;
    }
}

for my $change (@$changes) {
    next if $change->{kind} eq 'delete';
    my $rel = $change->{target_rel} // '';
    next unless $rel =~ /\.cs\z/;
    my $candidate_lines = $change->{candidate_lines};
    next unless defined $candidate_lines;
    my $reason = file_size_violation($change->{target_root}, $rel, $candidate_lines);
    if (defined $reason) {
        deny($reason);
        exit 0;
    }
}

for my $change (@$changes) {
    my @rels = grep { defined $_ } ($change->{source_rel}, $change->{target_rel});
    next unless grep { $_ eq '.filesize-baseline' } @rels;
    my $reason = 'Hand-editing .filesize-baseline raises a frozen size, exempts a file, or removes the KESIZE ratchet. That is the user\'s call, not the agent\'s. Say what changes and why it is legitimate. Ratcheting down after a file shrank is free: run scripts/check-file-size.sh --update instead.';
    if (($input->{tool_name} // '') eq 'apply_patch') {
        deny("$reason Codex PreToolUse hooks cannot request approval, so this edit is blocked until the owner handles it through an approved path.");
    } else {
        decide('ask', $reason);
    }
    exit 0;
}

exit 0;

sub normalize_changes {
    my ($payload, $workdir, $repo_root, $hook_phase) = @_;
    my $tool = $payload->{tool_name} // '';
    my $tool_input = ref $payload->{tool_input} eq 'HASH' ? $payload->{tool_input} : {};
    my $command = $tool_input->{command};
    $command =~ s/\A\s+|\s+\z//g if defined $command;

    if (defined $command && $command =~ /\A\*\*\* Begin Patch(?:\r?\n|\z)/) {
        return parse_apply_patch($command, $workdir, $repo_root, $hook_phase);
    }

    return ([], undef) unless $tool eq 'Write' || $tool eq 'Edit';
    my $path = $tool_input->{file_path} // '';
    return ([], undef) unless length $path;
    my ($abs, $rel, $path_root) = resolve_path($path, $workdir, $repo_root);

    if ($tool eq 'Write') {
        my $content = $tool_input->{content} // '';
        return ([{
            kind            => 'write',
            source_rel      => $rel,
            target_rel      => $rel,
            source_root     => $path_root,
            target_root     => $path_root,
            source_abs      => $abs,
            target_abs      => $abs,
            added           => $content,
            candidate_lines => newline_count($content),
        }], undef);
    }

    my $current = read_text($abs);
    return ([], "cannot read $rel") unless defined $current;
    my $old = $tool_input->{old_string} // '';
    my $new = $tool_input->{new_string} // '';
    my $candidate = $current;
    if ($tool_input->{replace_all}) {
        $candidate =~ s/\Q$old\E/$new/g;
    } else {
        my $at = index($candidate, $old);
        substr($candidate, $at, length($old), $new) if $at >= 0;
    }
    return ([{
        kind            => 'edit',
        source_rel      => $rel,
        target_rel      => $rel,
        source_root     => $path_root,
        target_root     => $path_root,
        source_abs      => $abs,
        target_abs      => $abs,
        added           => $new,
        candidate_lines => newline_count($candidate),
    }], undef);
}

sub parse_apply_patch {
    my ($command, $workdir, $repo_root, $hook_phase) = @_;
    $command =~ s/\r\n/\n/g;
    my @lines = split /\n/, $command, -1;
    pop @lines while @lines && $lines[-1] eq '';
    return ([], 'missing patch header') unless @lines && shift(@lines) eq '*** Begin Patch';
    return ([], 'missing patch footer') unless @lines && pop(@lines) eq '*** End Patch';

    my @changes;
    while (@lines) {
        my $header = shift @lines;
        next unless $header =~ /^\*\*\* (Add|Update|Delete) File: (.+)\z/;
        my ($kind, $source_path) = (lc($1), $2);
        my $target_path = $source_path;
        if ($kind eq 'update' && @lines && $lines[0] =~ /^\*\*\* Move to: (.+)\z/) {
            shift @lines;
            $target_path = $1;
        }

        my @body;
        push @body, shift @lines
            while @lines && $lines[0] !~ /^\*\*\* (?:Add|Update|Delete) File: /;

        my ($source_abs, $source_rel, $source_root) = resolve_path($source_path, $workdir, $repo_root);
        my ($target_abs, $target_rel, $target_root) = resolve_path($target_path, $workdir, $repo_root);
        my $change = {
            kind        => $kind,
            source_rel  => $source_rel,
            target_rel  => $target_rel,
            source_root => $source_root,
            target_root => $target_root,
            source_abs  => $source_abs,
            target_abs  => $target_abs,
        };

        if ($kind eq 'delete') {
            push @changes, $change;
            next;
        }

        my @added = map { /^\+(.*)\z/ ? $1 : () } @body;
        $change->{added} = join("\n", @added) . (@added ? "\n" : '');
        if ($hook_phase eq 'pre' && $target_rel =~ /\.cs\z/) {
            if ($kind eq 'add') {
                $change->{candidate_lines} = scalar @added;
            } else {
                my $current = read_text($source_abs);
                if (defined $current) {
                    my $removed = grep { /^-/ } @body;
                    $change->{candidate_lines} = logical_line_count($current) + scalar(@added) - $removed;
                }
            }
        }
        push @changes, $change;
    }

    return (\@changes, undef);
}

sub resolve_path {
    my ($path, $workdir, $repo_root) = @_;
    my $abs = File::Spec->file_name_is_absolute($path)
        ? File::Spec->canonpath($path)
        : File::Spec->canonpath(File::Spec->rel2abs($path, $workdir));
    $abs = real_path_with_missing_leaf($abs);
    my $path_root = repo_for_path($abs) // $repo_root;
    my $rel = File::Spec->abs2rel($abs, $path_root);
    $rel =~ s{\\}{/}g;
    return ($abs, $rel, $path_root);
}

sub real_path_with_missing_leaf {
    my ($path) = @_;
    my @tail;
    my $cursor = $path;
    while (!-e $cursor) {
        my $parent = dirname($cursor);
        last if $parent eq $cursor;
        unshift @tail, basename($cursor);
        $cursor = $parent;
    }
    my $resolved = abs_path($cursor) // File::Spec->canonpath($cursor);
    return @tail ? File::Spec->catfile($resolved, @tail) : $resolved;
}

sub read_text {
    my ($path) = @_;
    open my $file, '<:encoding(UTF-8)', $path or return undef;
    local $/;
    return <$file>;
}

sub repo_for_path {
    my ($path) = @_;
    my $probe = -d $path ? $path : dirname($path);
    while (!-d $probe) {
        my $parent = dirname($probe);
        return undef if $parent eq $probe;
        $probe = $parent;
    }
    return git_output($probe, 'rev-parse', '--show-toplevel');
}

sub newline_count {
    my ($text) = @_;
    return 0 unless defined $text && length $text;
    return scalar(() = $text =~ /\n/g);
}

sub logical_line_count {
    my ($text) = @_;
    return 0 unless defined $text && length $text;
    return newline_count($text) + ($text =~ /\n\z/ ? 0 : 1);
}

sub file_size_violation {
    my ($repo_root, $rel, $candidate_lines) = @_;
    my $script = "$repo_root/scripts/check-file-size.sh";
    return undef unless -f $script && -f "$repo_root/.filesize-baseline";

    my $stderr = gensym;
    my $original = getcwd();
    chdir $repo_root or return "Cannot enter repository root to check $rel.";
    my $pid = open3(my $child_in, my $child_out, $stderr, 'sh', $script, '--file', $rel);
    print {$child_in} "x\n" x $candidate_lines;
    close $child_in;
    my $stdout = do { local $/; <$child_out> // '' };
    my $errors = do { local $/; <$stderr> // '' };
    waitpid $pid, 0;
    my $status = $? >> 8;
    chdir $original or die "cannot restore working directory: $!";
    return undef if $status == 0;
    my $reason = $stdout . $errors;
    $reason =~ s/\s+\z//;
    return length $reason ? $reason : "File size check failed for $rel.";
}

sub version_changelog_reminder {
    my ($file_changes) = @_;
    my %version_paths;
    for my $change (@$file_changes) {
        my @locations = (
            [$change->{source_root}, $change->{source_rel}],
            [$change->{target_root}, $change->{target_rel}],
        );
        for my $location (@locations) {
            my ($repo_root, $rel) = @$location;
            next unless defined $repo_root && defined $rel;
            push @{$version_paths{$repo_root}}, $rel if $rel =~ /(?:\A|\/)Directory\.Build\.props\z/;
        }
    }
    return unless keys %version_paths;

    for my $repo_root (sort keys %version_paths) {
        my %seen;
        my @paths = grep { !$seen{$_}++ } @{$version_paths{$repo_root}};
        my $changed = 0;
        for my $rel (@paths) {
            my $diff = git_output($repo_root, 'diff', 'HEAD', '--', $rel) // '';
            if ($diff =~ /^[+-].*<KhaozEngineVersion>[0-9]/m) {
                $changed = 1;
                last;
            }
        }
        next unless $changed;
        next unless -f "$repo_root/CHANGELOG.md";
        system('git', '-C', $repo_root, 'diff', 'HEAD', '--quiet', '--', 'CHANGELOG.md');
        next unless ($? >> 8) == 0;

        print encode_json({ systemMessage => '<KhaozEngineVersion> changed but CHANGELOG.md is unmodified. Repo rule: the version bump and CHANGELOG entry go in the same commit.' });
        return;
    }
}

sub git_output {
    my ($dir, @args) = @_;
    open my $git, '-|', 'git', '-C', $dir, @args or return undef;
    local $/;
    my $output = <$git>;
    close $git or return undef;
    $output //= '';
    $output =~ s/\s+\z//;
    return $output;
}

sub deny {
    my ($reason) = @_;
    decide('deny', $reason);
}

sub decide {
    my ($decision, $reason) = @_;
    print encode_json({
        hookSpecificOutput => {
            hookEventName            => 'PreToolUse',
            permissionDecision       => $decision,
            permissionDecisionReason => $reason,
        },
    });
}
